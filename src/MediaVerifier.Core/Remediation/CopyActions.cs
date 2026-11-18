using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Rules;

namespace MediaVerifier.Core.Remediation;

/// <summary>
/// Writes the largest intact embedded preview of a damaged JPEG or RAW photo as <c>name.preview.jpg</c>
/// (subplan 12, <c>ACT-EXTRACT-PREVIEW</c>; task R-08). Embedded JPEG streams are found by their markers; each
/// candidate, largest first, must decode without an error before it is kept.
/// </summary>
public sealed class ExtractPreviewAction : IRemediationAction
{
    /// <summary>Previews smaller than this are thumbnails nobody wants back.</summary>
    public const int MinBytes = 4 * 1024;

    public string Id => ActionIds.ExtractPreview;

    public ActionClass Class => ActionClass.Copy;

    /// <summary>Every embedded JPEG stream (SOI <c>FF D8 FF</c> to the following EOI <c>FF D9</c>) other than one at offset 0, largest first.</summary>
    public static List<(long Offset, int Length)> Candidates(ReadOnlySpan<byte> data)
    {
        List<(long, int)> found = [];
        ReadOnlySpan<byte> soi = [0xFF, 0xD8, 0xFF];
        ReadOnlySpan<byte> eoi = [0xFF, 0xD9];
        int start = 1;
        while (start < data.Length)
        {
            int at = data[start..].IndexOf(soi);
            if (at < 0)
            {
                break;
            }

            at += start;
            int end = data[(at + 3)..].IndexOf(eoi);
            if (end < 0)
            {
                break;
            }

            int length = end + 3 + 2;
            if (length >= MinBytes)
            {
                found.Add((at, length));
            }

            start = at + 3;
        }

        found.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return found;
    }

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[] data = await File.ReadAllBytesAsync(context.SourcePath, token).ConfigureAwait(false);
        foreach ((long offset, int length) in Candidates(data).Take(8))
        {
            await File.WriteAllBytesAsync(context.TempPath, data.AsMemory((int)offset, length), token).ConfigureAwait(false);
            if (await DecodesCleanlyAsync(context, token).ConfigureAwait(false))
            {
                context.FinalName = Path.GetFileNameWithoutExtension(context.SourcePath) + ".preview.jpg";
                context.Detail["previewBytes"] = length;
                return;
            }
        }

        File.Delete(context.TempPath);
        throw new InvalidOperationException(CoreStrings.Remediation.NoPreview);
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token) =>
        await DecodesCleanlyAsync(context, token).ConfigureAwait(false) ? null : CoreStrings.Remediation.OutputUnreadable;

    private static async Task<bool> DecodesCleanlyAsync(OperationContext context, CancellationToken token)
    {
        FfResult result = await Ff.RunAsync(context, ["-xerror", "-f", "jpeg_pipe", "-i", context.TempPath, "-f", "null", "-"], token, "error").ConfigureAwait(false);
        return result.Completed && !result.Log.Has(LogSignalKind.DecodeError) && !result.Log.Has(LogSignalKind.OpenFailure) && !result.Log.Has(LogSignalKind.Truncation)
            && !result.Log.Has(LogSignalKind.Unknown);
    }
}

/// <summary>
/// An experimental de-clipped copy of a lossless source as <c>name.declipped.flac</c>, 24-bit, 6 dB quieter, never
/// replacing the original (subplan 12, <c>ACT-DECLIP-COPY</c>; task R-09). The copy is an estimate of the lost peaks.
/// </summary>
public sealed class DeclipCopyAction : IRemediationAction
{
    public string Id => ActionIds.DeclipCopy;

    public ActionClass Class => ActionClass.Copy;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        FfResult result = await Ff.RunAsync(
            context,
            ["-i", context.SourcePath, "-map", "0:a:0", "-af", "volume=-6dB,adeclip", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-f", "flac", context.TempPath],
            token).ConfigureAwait(false);
        if (!result.Completed || !File.Exists(context.TempPath))
        {
            throw new IOException(CoreStrings.Remediation.ToolFailed);
        }

        context.FinalName = Path.GetFileNameWithoutExtension(context.SourcePath) + ".declipped.flac";
        context.Detail["estimate"] = true;
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        double? peak = null;
        FfResult result = await Ff.RunAsync(
            context,
            ["-f", "flac", "-i", context.TempPath, "-af", "volumedetect", "-f", "null", "-"],
            token,
            "info",
            line =>
            {
                int at = line.Raw.IndexOf("max_volume:", StringComparison.Ordinal);
                if (at >= 0)
                {
                    string number = line.Raw[(at + 11)..].Replace("dB", string.Empty, StringComparison.Ordinal).Trim();
                    peak = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) ? db : peak;
                }
            }).ConfigureAwait(false);
        return result.Completed && peak is double p && p <= 0 ? null : CoreStrings.Remediation.OutputUnreadable;
    }
}

/// <summary>
/// Moves the file to the Recycle Bin (subplan 12, <c>ACT-RECYCLE</c>). Windows restores it from there; MediaVerifier
/// cannot undo it. Not offered on volumes without a Recycle Bin (network shares).
/// </summary>
public sealed class RecycleAction : IRemediationAction
{
    private const int FoDelete = 3;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofSilent = 0x04;
    private const ushort FofNoErrorUi = 0x400;

    public string Id => ActionIds.Recycle;

    public ActionClass Class => ActionClass.Recycle;

    public Task ProduceAsync(OperationContext context, CancellationToken token) => Task.CompletedTask;

    public Task<string?> VerifyAsync(OperationContext context, CancellationToken token) => Task.FromResult<string?>(null);

    /// <summary>Sends one file to the Recycle Bin without any dialog; throws when the shell refuses.</summary>
    public static void Send(string path)
    {
        ShFileOpStruct operation = new()
        {
            Func = FoDelete,
            From = path + "\0\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
        {
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The Recycle Bin refused the file (code {result})."));
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public int Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }
}
