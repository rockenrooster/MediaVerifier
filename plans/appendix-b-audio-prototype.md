# Appendix B — Audio analysis reference prototype

This is the throwaway program used to validate the algorithms in
[subplan 10](10-audio-quality-detectors.md) before they were written into the plan. It is reference
material, not project code: the repository contains no source yet. Its output on the generated
fixtures is the "Verified prototype results" table in subplan 10.

What it demonstrates:

- decoding through `ffmpeg` into a 32-bit float WAV pipe and parsing the streamed header;
- effective bit depth from the OR of scaled integer samples;
- clipping as runs of three or more samples on the peak plateau (the product adds the edge-step
  test and a streaming histogram, see subplan 10);
- the long-term average spectrum, the terminal-drop search and the knee refinement.

What it deliberately does not do: it buffers the whole track in memory, uses `double` FFTs without
SIMD, has no dropout, stereo or DC-window logic, and prints a table instead of producing findings.
Port the logic, not the structure.

To reproduce the numbers, save the listing as `AudioProbe.cs` in an empty folder outside the
repository and run it with the .NET 10 SDK (file-based program, no project needed) and `ffmpeg` on
`PATH`:

```
dotnet run AudioProbe.cs -- src16.flac fake128.flac up96.flac pad24.flac clip.flac
```

## Listing

```csharp
// Reference prototype for MediaVerifier audio analysis (spectral cutoff, clipping, effective bit depth).
// Run: dotnet run AudioProbe.cs -- <file> [<file>...]
// Decodes through ffmpeg (must be on PATH) into 32-bit float WAV on stdout and analyses the samples.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0) { Console.Error.WriteLine("usage: AudioProbe <file>..."); return 1; }
Console.WriteLine($"{"file",-18} {"sr",6} {"ch",2} {"sec",6} {"bits",5} {"peakdB",7} {"dc",8} {"runs",6} {"clip%",7} {"maxRun",6} {"overs",6} | {"cutHz",6} {"drop",5} {"above",6} {"ratio",5} | verdict");
foreach (string path in args) Analyze(path);
return 0;

static void Analyze(string path)
{
    ProcessStartInfo psi = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string a in new[] { "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-err_detect", "crccheck+bitstream+buffer",
                 "-i", path, "-map", "0:a:0", "-map_metadata", "-1", "-fflags", "+bitexact", "-c:a", "pcm_f32le", "-f", "wav", "-" })
        psi.ArgumentList.Add(a);

    using Process p = Process.Start(psi)!;
    Task<string> stderr = p.StandardError.ReadToEndAsync();
    using Stream s = p.StandardOutput.BaseStream;
    (int sr, int ch) = ReadWavHeader(s);

    // Prototype keeps everything in memory; production must stream.
    List<float[]> blocks = [];
    long total = 0;
    while (true)
    {
        byte[] raw = new byte[1 << 20];
        int n = ReadFull(s, raw);
        if (n == 0) break;
        float[] f = new float[n / 4];
        Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        blocks.Add(f); total += f.Length;
        if (n < raw.Length) break;
    }
    p.WaitForExit();
    float[] x = new float[total];
    int o = 0; foreach (float[] b in blocks) { b.CopyTo(x, o); o += b.Length; }
    long frames = total / ch;

    // ---- sample-domain statistics -------------------------------------------------------------
    double peak = 0, sum = 0; long overs = 0; int orBits = 0; bool integral = true;
    for (long i = 0; i < total; i++)
    {
        float v = x[i]; double a = Math.Abs(v);
        if (a > peak) peak = a;
        if (a > 1.0) overs++;
        sum += v;
        if (integral)
        {
            double scaled = v * 8388608.0; double r = Math.Round(scaled);
            if (Math.Abs(scaled - r) > 1e-3) integral = false; else orBits |= (int)r;
        }
    }
    int effBits = integral ? (orBits == 0 ? 0 : 24 - System.Numerics.BitOperations.TrailingZeroCount(orBits)) : -1;
    double dc = sum / Math.Max(1, total);

    // ---- clipping: runs of >= 3 consecutive samples sitting on the peak plateau, per channel --------
    // tolerance: one LSB of the effective depth for integer PCM, 0.05 dB for float/lossy decodes.
    double tol = integral && effBits > 0 ? 1.0 / (1 << (effBits - 1)) : peak * (1 - Math.Pow(10, -0.05 / 20));
    long clipRuns = 0, clipSamples = 0; int maxRun = 0;
    for (int c = 0; c < ch; c++)
    {
        int run = 0; int sign = 0;
        for (long i = c; i < total; i += ch)
        {
            float v = x[i]; int sg = v > 0 ? 1 : -1;
            if (Math.Abs(v) >= peak - tol && (run == 0 || sg == sign)) { run++; sign = sg; }
            else
            {
                if (run >= 3) { clipRuns++; clipSamples += run; if (run > maxRun) maxRun = run; }
                run = Math.Abs(v) >= peak - tol ? 1 : 0; sign = sg;
            }
        }
        if (run >= 3) { clipRuns++; clipSamples += run; if (run > maxRun) maxRun = run; }
    }

    // ---- long-term average spectrum of the mid channel ---------------------------------------------
    int N = sr <= 50000 ? 4096 : sr <= 100000 ? 8192 : 16384;
    double[] win = new double[N]; for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
    double[] acc = new double[N / 2 + 1]; double[] re = new double[N], im = new double[N]; long used = 0;
    for (long f0 = 0; f0 + N <= frames; f0 += N)
    {
        double e = 0;
        for (int i = 0; i < N; i++)
        {
            double m = 0; for (int c = 0; c < ch; c++) m += x[(f0 + i) * ch + c];
            m /= ch; e += m * m; re[i] = m * win[i]; im[i] = 0;
        }
        if (e / N < 1e-7) continue; // skip frames below about -70 dBFS RMS
        Fft(re, im);
        for (int k = 0; k <= N / 2; k++) acc[k] += re[k] * re[k] + im[k] * im[k];
        used++;
    }
    double binHz = (double)sr / N; double nyq = sr / 2.0;
    double[] db = new double[acc.Length];
    int half = Math.Max(1, (int)(100 / binHz)); // +-100 Hz power smoothing
    for (int k = 0; k < acc.Length; k++)
    {
        double a = 0; int cnt = 0;
        for (int j = Math.Max(1, k - half); j <= Math.Min(acc.Length - 1, k + half); j++) { a += acc[j]; cnt++; }
        db[k] = 10 * Math.Log10(a / Math.Max(1, cnt) / Math.Max(1, used) / ((double)N * N / 4) + 1e-30);
    }

    // ---- terminal-drop cutoff search ----------------------------------------------------------------
    // For each candidate f: below = mean dB in [f-1000, f-200], above = mean dB in [f+200, f+1000].
    // Accept only if everything above f+200 stays at least 15 dB under 'below' (terminal, not a notch).
    double bestF = nyq, bestDrop = 0, bestAbove = 0;
    for (double f = 4000; f <= nyq - 500; f += 50)
    {
        double below = MeanDb(db, binHz, f - 1000, f - 200);
        double above = MeanDb(db, binHz, f + 200, Math.Min(f + 1000, nyq - 50));
        double maxAbove = MaxDb(db, binHz, f + 200, nyq - 50);
        if (maxAbove > below - 15) continue;
        double drop = below - above;
        if (drop > bestDrop) { bestDrop = drop; bestF = f; bestAbove = above; }
    }
    // Refine to the knee: walk down from the steepest region while the local slope stays steep
    // (>= 4 dB per 500 Hz). Slow resampler transitions and sharp codec lowpasses both end at the knee.
    if (bestDrop > 0)
    {
        double f = bestF;
        while (f - 250 > 3000)
        {
            double slope = db[(int)((f - 250) / binHz)] - db[(int)((f + 250) / binHz)];
            if (slope < 4) break;
            f -= 50;
        }
        bestF = f + 250;
    }
    double ratio = bestF / nyq;
    string verdict = bestDrop >= 25 && ratio < 0.93 ? $"BRICKWALL @ {bestF / 1000:0.0} kHz" : bestDrop >= 12 && ratio < 0.93 ? "soft rolloff" : "full band";

    string warn = stderr.Result.Trim();
    Console.WriteLine($"{Path.GetFileName(path),-18} {sr,6} {ch,2} {frames / (double)sr,6:0.0} {(effBits < 0 ? "float" : effBits.ToString()),5} {20 * Math.Log10(peak + 1e-12),7:0.00} {dc,8:0.00000} {clipRuns,6} {100.0 * clipSamples / Math.Max(1, total),7:0.000} {maxRun,6} {overs,6} | {bestF,6:0} {bestDrop,5:0.0} {bestAbove,6:0.0} {ratio,5:0.00} | {verdict}{(warn.Length > 0 ? "  [log: " + warn.Split('\n')[0].Trim() + "]" : "")}");
}

static double MeanDb(double[] db, double binHz, double f0, double f1)
{
    int a = Math.Clamp((int)(f0 / binHz), 1, db.Length - 1), b = Math.Clamp((int)(f1 / binHz), a, db.Length - 1);
    double s = 0; for (int k = a; k <= b; k++) s += db[k]; return s / (b - a + 1);
}

static double MaxDb(double[] db, double binHz, double f0, double f1)
{
    int a = Math.Clamp((int)(f0 / binHz), 1, db.Length - 1), b = Math.Clamp((int)(f1 / binHz), a, db.Length - 1);
    double m = double.MinValue; for (int k = a; k <= b; k++) m = Math.Max(m, db[k]); return m;
}

static (int sampleRate, int channels) ReadWavHeader(Stream s)
{
    byte[] h = new byte[12]; s.ReadExactly(h);
    if (Encoding.ASCII.GetString(h, 0, 4) != "RIFF" || Encoding.ASCII.GetString(h, 8, 4) != "WAVE") throw new InvalidDataException("not a WAV stream");
    int sr = 0, ch = 0; byte[] c = new byte[8];
    while (true)
    {
        s.ReadExactly(c);
        string id = Encoding.ASCII.GetString(c, 0, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(4));
        if (id == "data") return (sr, ch);
        byte[] body = new byte[size + (size & 1)]; s.ReadExactly(body);
        if (id == "fmt ")
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            ch = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
            sr = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
            ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14));
            if ((tag != 3 && tag != 0xFFFE) || bits != 32) throw new InvalidDataException($"unexpected WAV format tag {tag} / {bits} bits");
        }
    }
}

static int ReadFull(Stream s, byte[] buf)
{
    int got = 0;
    while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n == 0) break; got += n; }
    return got - got % 4;
}

static void Fft(double[] re, double[] im)
{
    int n = re.Length;
    for (int i = 1, j = 0; i < n; i++)
    {
        int bit = n >> 1;
        for (; (j & bit) != 0; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
    }
    for (int len = 2; len <= n; len <<= 1)
    {
        double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
        for (int i = 0; i < n; i += len)
        {
            double cr = 1, ci = 0;
            for (int k = 0; k < len / 2; k++)
            {
                int a = i + k, b = a + len / 2;
                double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
            }
        }
    }
}
```

## Sample output

FFmpeg 8.0.1, fixtures from subplan 15. Columns: sample rate, channels, seconds, effective bits,
peak dBFS, DC, clipped runs, clipped %, longest run, samples above full scale, knee Hz, drop dB,
level above the knee dB, knee ÷ Nyquist.

```
file                   sr ch    sec  bits  peakdB       dc   runs   clip% maxRun  overs |  cutHz  drop  above ratio | verdict
src16.flac          44100  2   20.0    16  -10.29  0.00312      0   0.000      0      0 |  22050   0.0    0.0  1.00 | full band
pad24.flac          44100  2   20.0    16  -10.29  0.00312      0   0.000      0      0 |  22050   0.0    0.0  1.00 | full band
true96.flac         96000  2   20.0    24   -9.20 -0.00037      0   0.000      0      0 |  48000   0.0    0.0  1.00 | full band
up96.flac           96000  2   20.0    24   -9.93  0.00312      0   0.000      0      0 |  23200  47.7 -158.9  0.48 | BRICKWALL @ 23.2 kHz
fake128.flac        44100  2   20.0    16  -10.71  0.00298      0   0.000      0      0 |  16550  67.5 -134.4  0.75 | BRICKWALL @ 16.6 kHz
fake320.flac        44100  2   20.0    16  -10.30  0.00312      0   0.000      0      0 |  20000  68.0 -135.2  0.91 | BRICKWALL @ 20.0 kHz
fakeV0.flac         44100  2   20.0    16  -10.30  0.00313      0   0.000      0      0 |  22050   0.0    0.0  1.00 | full band
fakeAac.flac        44100  2   20.0    16  -10.19  0.00312      0   0.000      0      0 |  17200  68.5 -135.4  0.78 | BRICKWALL @ 17.2 kHz
fakeOpus.flac       48000  2   20.0    16  -10.70 -0.00005      0   0.000      0      0 |  20000  57.2 -126.5  0.83 | BRICKWALL @ 20.0 kHz
m128.mp3            44100  2   20.0 float  -10.71  0.00298      0   0.000      0      0 |  16550  84.0 -152.1  0.75 | BRICKWALL @ 16.6 kHz
m320.mp3            44100  2   20.0 float  -10.30  0.00312      0   0.000      0      0 |  20000  86.7 -154.7  0.91 | BRICKWALL @ 20.0 kHz
infl320.mp3         44100  2   20.0 float  -10.71  0.00298      0   0.000      0      0 |  16550  84.0 -152.1  0.75 | BRICKWALL @ 16.6 kHz
a128.m4a            44100  2   20.0 float  -10.19  0.00312      0   0.000      0      0 |  17200  77.8 -145.3  0.78 | BRICKWALL @ 17.2 kHz
o128.opus           48000  2   20.0 float  -10.70 -0.00005      0   0.000      0      0 |  20000  58.4 -127.6  0.83 | BRICKWALL @ 20.0 kHz
natural.flac        44100  2   20.0    16  -10.45  0.00312      0   0.000      0      0 |  22050   0.0    0.0  1.00 | full band
dull.flac           44100  2   20.0    16  -11.58  0.00312      0   0.000      0      0 |  22050   0.0    0.0  1.00 | full band
clip.flac           44100  2   20.0    16    0.00  0.01569    406   0.095     14      0 |  22050   0.0    0.0  1.00 | full band
clipatt.flac        44100  2   20.0    16   -3.00  0.01111    406   0.095     14      0 |  22050   0.0    0.0  1.00 | full band
clip320.mp3         44100  2   20.0 float    0.72  0.01570      0   0.000      0   2550 |  20334  86.7 -140.8  0.92 | BRICKWALL @ 20.3 kHz
```

Notes on reading it:

- The prototype's verdict column is a single crude rule (`drop ≥ 25 dB` and ratio below 0.93). The
  product uses the tables in subplan 10, which is why `m320.mp3` is not a finding there: a 320 kbps
  MP3 is expected to stop near 20 kHz.
- The clipped fixtures show a DC value of 0.011–0.016 because hard clipping of noise is not perfectly
  symmetric; that is why `AUD-DC` requires a stable sign across one-second windows.
- `clip320.mp3` was measured before the knee refinement was added (20,334 Hz); the other rows were
  measured after it.