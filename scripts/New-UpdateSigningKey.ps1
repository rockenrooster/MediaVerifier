<#
.SYNOPSIS
Creates the update-signing key pair (subplan 14, task U-01). Run by the owner only.

.DESCRIPTION
Writes an RSA 3072 private key (PKCS#8 PEM) and its public key to -OutDir, which must be outside the
repository. Paste the public key into UpdateTrust.PublicKeyPem
(src\MediaVerifier.Core\Updates\UpdateService.cs) and store the private key as the GitHub secret
MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM. Never commit the private key. The script refuses to
overwrite existing files and refuses an output folder inside the repository.

.EXAMPLE
.\scripts\New-UpdateSigningKey.ps1 -OutDir "$env:USERPROFILE\Secrets\MediaVerifier"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Split-Path $PSScriptRoot -Parent)).Path.TrimEnd('\') + '\'
$target = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\') + '\'
if ($target.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write keys inside the repository ($repo). Choose a folder outside it."
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
$privatePath = Join-Path $target "mediaverifier-update-signing.private.pem"
$publicPath = Join-Path $target "mediaverifier-update-signing.public.pem"
if ((Test-Path $privatePath) -or (Test-Path $publicPath)) {
    throw "Key files already exist in $target; refusing to overwrite them."
}

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    [System.IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem())
    [System.IO.File]::WriteAllText($publicPath, $rsa.ExportSubjectPublicKeyInfoPem())

    # Round trip: a test manifest signed with the private key verifies with the public key.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1}')
    $signature = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $check = [System.Security.Cryptography.RSA]::Create()
    $check.ImportFromPem([System.IO.File]::ReadAllText($publicPath))
    if (-not $check.VerifyData($bytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw "The new key pair did not verify its own signature."
    }
    $check.Dispose()
}
finally {
    $rsa.Dispose()
}

Write-Host "Private key: $privatePath (keep it secret; store it as MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM)" -ForegroundColor Yellow
Write-Host "Public key:  $publicPath (paste it into UpdateTrust.PublicKeyPem)" -ForegroundColor Green
Get-Content $publicPath
