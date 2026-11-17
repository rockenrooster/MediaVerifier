using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// Task U-01's acceptance for scripts/New-UpdateSigningKey.ps1, as a dry run: a throwaway key pair in a temporary
/// folder (deleted afterwards, never the production key) signs a manifest that UpdateTrust verifies; the script
/// refuses to overwrite keys and refuses a folder inside the repository.
/// </summary>
[Trait("Category", "System")]
public sealed class SigningKeyScriptTests
{
    [PwshFact]
    public void A_dry_run_key_round_trips_and_the_script_refuses_to_overwrite_or_write_in_the_repository()
    {
        using TempDirectory temp = new("keys");
        string folder = Path.Combine(temp.Path, "out");
        (int exit, string output) = Run(folder);
        Assert.True(exit == 0, output);
        string privatePem = File.ReadAllText(Path.Combine(folder, "mediaverifier-update-signing.private.pem"));
        string publicPem = File.ReadAllText(Path.Combine(folder, "mediaverifier-update-signing.public.pem"));
        Assert.Contains("BEGIN PRIVATE KEY", privatePem, StringComparison.Ordinal);
        Assert.Contains("BEGIN PUBLIC KEY", publicPem, StringComparison.Ordinal);

        // What build.ps1 does with the private key: sign the exact manifest bytes; the app verifies with the public key.
        byte[] manifest = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"version":"0.1.0.2","tag":"v0.1.0.2"}""");
        byte[] signature;
        using (RSA rsa = RSA.Create())
        {
            rsa.ImportFromPem(privatePem);
            signature = rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        Assert.True(UpdateTrust.VerifyManifestSignature(manifest, signature, publicPem));
        byte[] tampered = (byte[])manifest.Clone();
        tampered[^3] ^= 1;
        Assert.False(UpdateTrust.VerifyManifestSignature(tampered, signature, publicPem));

        (int again, string againOutput) = Run(folder);
        Assert.NotEqual(0, again);
        Assert.Contains("refusing to overwrite", againOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(privatePem, File.ReadAllText(Path.Combine(folder, "mediaverifier-update-signing.private.pem")));

        string inside = RepositoryPaths.Combine("artifacts", "key-test-" + Guid.NewGuid().ToString("N"));
        (int inRepo, string inRepoOutput) = Run(inside);
        Assert.NotEqual(0, inRepo);
        Assert.Contains("inside the repository", inRepoOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(inside));
    }

    private static (int Exit, string Output) Run(string outDir)
    {
        ProcessStartInfo start = new(PwshFactAttribute.Pwsh!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", RepositoryPaths.Combine("scripts", "New-UpdateSigningKey.ps1"), "-OutDir", outDir })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), "the key script did not finish");
        return (process.ExitCode, output + error.Result);
    }
}

/// <summary>A test that needs PowerShell 7 (pwsh.exe) on PATH.</summary>
public sealed class PwshFactAttribute : FactAttribute
{
    public static readonly string? Pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe"))
        .FirstOrDefault(File.Exists);

    public PwshFactAttribute()
    {
        if (Pwsh is null)
        {
            Skip = "PowerShell 7 (pwsh.exe) is not on PATH.";
        }
    }
}
