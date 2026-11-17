<#
.SYNOPSIS
Fetches the pinned FFmpeg bundle into deps\ffmpeg\bin (subplan 17, task FFB-06).

.DESCRIPTION
Reads deps\ffmpeg\bundle.lock.json, downloads the asset from this repository's bundle release
(gh release download when the GitHub CLI is available, HTTPS otherwise), verifies its size and SHA-256,
then extracts it. Fails closed on any mismatch and never falls back to another source. Idempotent: a
bin folder whose bundle.json matches the lock is left alone.
#>
[CmdletBinding()]
param(
    [string]$Repository = "rockenrooster/MediaVerifier"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$lockPath = Join-Path $root "deps\ffmpeg\bundle.lock.json"
$binDir = Join-Path $root "deps\ffmpeg\bin"

if (-not (Test-Path $lockPath)) {
    Write-Warning "No FFmpeg bundle has been published yet ($lockPath is missing). For development, set MEDIAVERIFIER_FFMPEG_DIR to a folder with ffmpeg.exe and ffprobe.exe."
    return
}

$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
foreach ($field in @("release", "asset", "sha256", "sizeBytes", "ffmpegVersion")) {
    if (-not $lock.$field) {
        throw "bundle.lock.json has no '$field'."
    }
}

if ($lock.sha256 -notmatch '^[0-9a-f]{64}$') {
    throw "bundle.lock.json: sha256 must be 64 lower-case hex digits."
}

# Already in place?
$installed = Join-Path $binDir "bundle.json"
$marker = Join-Path $binDir ".lock-sha256"
if ((Test-Path $installed) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $lock.sha256)) {
    Write-Host "FFmpeg bundle $($lock.release) is already in $binDir"
    return
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("mediaverifier-bundle-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work $lock.asset
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        & gh release download $lock.release --repo $Repository --pattern $lock.asset --dir $work
        if ($LASTEXITCODE -ne 0) {
            throw "gh release download failed"
        }
    }
    else {
        $url = "https://github.com/$Repository/releases/download/$($lock.release)/$($lock.asset)"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    }

    $item = Get-Item $zip
    if ($item.Length -ne [long]$lock.sizeBytes) {
        throw "Size mismatch: expected $($lock.sizeBytes) bytes, got $($item.Length). Nothing was extracted."
    }

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $lock.sha256) {
        throw "SHA-256 mismatch: expected $($lock.sha256), got $hash. Nothing was extracted."
    }

    $staging = Join-Path $work "extract"
    Expand-Archive -Path $zip -DestinationPath $staging
    if (-not (Test-Path (Join-Path $staging "ffmpeg.exe")) -or -not (Test-Path (Join-Path $staging "bundle.json"))) {
        throw "The bundle does not contain ffmpeg.exe and bundle.json at its top level."
    }

    # Every file must match bundle.json before anything replaces the current folder.
    $bundle = Get-Content (Join-Path $staging "bundle.json") -Raw | ConvertFrom-Json
    foreach ($entry in $bundle.files.PSObject.Properties) {
        $file = Join-Path $staging $entry.Name
        if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            throw "bundle.json does not match $($entry.Name)."
        }
    }

    Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Split-Path $binDir -Parent) -Force | Out-Null
    Move-Item $staging $binDir
    Set-Content $marker $lock.sha256 -NoNewline
    Write-Host "FFmpeg $($lock.ffmpegVersion) bundle $($lock.release) extracted to $binDir" -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
