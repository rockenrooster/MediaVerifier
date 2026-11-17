using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Ipc;

/// <summary>Name of the GUI's command pipe for this Windows session.</summary>
public static class IpcNames
{
    public static string PipeNameForCurrentSession() =>
        "MediaVerifier.Ipc." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The GUI instance's command channel (subplan 01). One pipe instance at a time, readable only by the
/// current user. Messages longer than 8 KiB, malformed JSON and unknown commands are logged and
/// dropped; the server keeps listening.
/// </summary>
public sealed class IpcServer : IDisposable
{
    public const int MaxMessageBytes = 8 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _rejected;

    public IpcServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = pipeName;
    }

    public string PipeName { get; }

    /// <summary>Raised on a pool thread for every valid message. Handlers marshal to the UI thread.</summary>
    public event EventHandler<IpcMessage>? MessageReceived;

    /// <summary>Count of rejected messages (oversized, malformed or invalid).</summary>
    public int RejectedCount => Volatile.Read(ref _rejected);

    /// <summary>Set when the pipe could not be created (for example another process owns the name).</summary>
    public Exception? StartFailure { get; private set; }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => ListenAsync(_stop.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StartFailure = ex;
                AppLog.Shared?.Warning($"IPC pipe {PipeName} could not be created; a second launch cannot reach this instance.", ex);
                return;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await HandleConnectionAsync(server, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException ex)
                {
                    AppLog.Shared?.Warning("IPC connection failed", ex);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        byte[] buffer = new byte[MaxMessageBytes + 1];
        int length = 0;
        bool sawNewline = false;
        try
        {
            while (length < buffer.Length)
            {
                int read = await server.ReadAsync(buffer.AsMemory(length, buffer.Length - length), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                int newline = Array.IndexOf(buffer, (byte)'\n', length, read);
                length += read;
                if (newline >= 0)
                {
                    length = newline;
                    sawNewline = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Reject("timed out before a complete message arrived");
            return;
        }

        if (!sawNewline && length > MaxMessageBytes)
        {
            Reject($"message exceeds {MaxMessageBytes} bytes");
            return;
        }

        ReadOnlySpan<byte> payload = buffer.AsSpan(0, length).TrimEnd((byte)'\r');
        IpcMessage? message = IpcMessage.TryParse(payload);
        if (message is null)
        {
            Reject("malformed or unknown command");
            return;
        }

        MessageReceived?.Invoke(this, message);
    }

    private void Reject(string reason)
    {
        Interlocked.Increment(ref _rejected);
        AppLog.Shared?.Warning($"IPC message rejected: {reason}");
    }

    private NamedPipeServerStream CreateServer()
    {
        PipeSecurity security = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("No user SID.");

        // The client's CurrentUserOnly check compares the pipe's owner with its token's default owner, which is the
        // Administrators group in an elevated process: own the pipe the same way, as .NET's own CurrentUserOnly
        // servers do. Only this user may open it either way.
        security.SetOwner(identity.Owner ?? user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: MaxMessageBytes,
            outBufferSize: 0,
            security);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>Sends one command to the running GUI instance.</summary>
public static class IpcClient
{
    /// <summary>
    /// Connects, lets the receiving process take the foreground, writes one line, and waits for it to
    /// be read. Returns false when no instance is listening within <paramref name="timeout"/>.
    /// </summary>
    public static bool TrySend(string pipeName, IpcMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint serverProcessId))
            {
                // This launch was started by the user and may pass its foreground right on.
                _ = AllowSetForegroundWindow(serverProcessId);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonLine());
            client.Write(bytes);
            client.Flush();
            client.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Test helper: writes raw bytes, for oversized and malformed message tests.</summary>
    internal static bool TrySendRaw(string pipeName, ReadOnlySpan<byte> bytes, TimeSpan timeout)
    {
        try
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
