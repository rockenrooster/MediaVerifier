param(
    [string]$Version,
    [switch]$NoIncrement
)

# Builds the app folder, the portable zip and (with Inno Setup 6) the signed installer into artifacts\
# (subplan 14, task U-05; ported from FastTaskMgr build.ps1). The app is framework-dependent: the
# .NET 10 Desktop Runtime (x64) on the PC is a prerequisite (master plan D-01).

$ErrorActionPreference = "Stop"

function RunDotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed"
    }
}

function Get-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) {
        return $candidate
    }

    return $null
}

function Write-SignedUpdateManifest {
    param(
        [string]$Version,
        [string]$SetupPath,
        [string]$ManifestPath,
        [string]$SignaturePath
    )

    $setupItem = Get-Item $SetupPath
    $setupHash = (Get-FileHash $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestJson = [ordered]@{
        schemaVersion = 1
        version = $Version
        tag = "v$Version"
        assetName = "MediaVerifier-Setup.exe"
        downloadUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v$Version/MediaVerifier-Setup.exe"
        sha256 = $setupHash
        sizeBytes = $setupItem.Length
        createdUtc = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json -Compress

    [System.IO.File]::WriteAllText($ManifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

    $privateKey = $env:MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM
    if ([string]::IsNullOrWhiteSpace($privateKey) -and $env:MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM_FILE) {
        $privateKey = Get-Content $env:MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM_FILE -Raw
    }

    if ([string]::IsNullOrWhiteSpace($privateKey)) {
        Remove-Item $ManifestPath, $SignaturePath -Force -ErrorAction SilentlyContinue
        if ($env:GITHUB_ACTIONS -eq "true") {
            throw "MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM is required to sign update manifests."
        }

        Write-Warning "MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM is not set; skipped signed update manifest."
        return
    }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($privateKey)
        $manifestBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
        $signature = $rsa.SignData(
            $manifestBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [System.IO.File]::WriteAllBytes($SignaturePath, $signature)
    }
    finally {
        $rsa.Dispose()
    }
}

# The version lives in Directory.Build.props only (subplan 14).
$propsPath = Join-Path $PSScriptRoot "Directory.Build.props"
$csprojPath = Join-Path $PSScriptRoot "src\MediaVerifier\MediaVerifier.csproj"
$innoScript = Join-Path $PSScriptRoot "installer\MediaVerifier.iss"
$content = Get-Content $propsPath -Raw

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version must be x.y.z.w"
    }
    $newVersion = $Version
}
elseif ($content -match '<AssemblyVersion>(\d+)\.(\d+)\.(\d+)\.(\d+)</AssemblyVersion>') {
    if ($NoIncrement) {
        $newVersion = "$($matches[1]).$($matches[2]).$($matches[3]).$($matches[4])"
    }
    else {
        $newVersion = "$($matches[1]).$($matches[2]).$($matches[3]).$([int]$matches[4] + 1)"
    }
}
else {
    throw "AssemblyVersion not found in $propsPath"
}

$content = $content -replace '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$newVersion</AssemblyVersion>"
$content = $content -replace '<FileVersion>.*?</FileVersion>', "<FileVersion>$newVersion</FileVersion>"
Set-Content $propsPath -Value $content -NoNewline

$publishDir = Join-Path $PSScriptRoot "src\MediaVerifier\obj\script-publish"
$artifactDir = Join-Path $PSScriptRoot "artifacts"
$appDir = Join-Path $artifactDir "MediaVerifier"
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

RunDotnet clean $csprojPath -c Release
RunDotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o $publishDir

# The app folder: the executable, the SQLite native library and the bundled FFmpeg (subplan 17).
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
try {
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Copy-Item (Join-Path $publishDir "MediaVerifier.exe") $appDir -ErrorAction Stop
    Copy-Item (Join-Path $publishDir "e_sqlite3.dll") $appDir -ErrorAction Stop
}
catch {
    throw "Could not replace $appDir. Close any running MediaVerifier.exe from artifacts, then retry."
}

foreach ($doc in @("LICENSE", "THIRD-PARTY-NOTICES.md")) {
    $path = Join-Path $PSScriptRoot $doc
    if (Test-Path $path) {
        Copy-Item $path $appDir
    }
}

$bundleDir = Join-Path $PSScriptRoot "deps\ffmpeg\bin"
& (Join-Path $PSScriptRoot "scripts\Get-FfmpegBundle.ps1") -ErrorAction Continue
if (Test-Path (Join-Path $bundleDir "ffmpeg.exe")) {
    Copy-Item $bundleDir (Join-Path $appDir "ffmpeg") -Recurse
}
elseif ($env:GITHUB_ACTIONS -eq "true") {
    throw "The FFmpeg bundle is required for a release build."
}
else {
    Write-Warning "No FFmpeg bundle in deps\ffmpeg\bin; the app folder has no media engine (scanning is disabled until one is added)."
}

# Portable zip: the whole folder (the app is more than one file).
$zip = Join-Path $artifactDir "MediaVerifier-portable.zip"
$zipSha = "$zip.sha256"
Remove-Item $zip, $zipSha -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zip
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  MediaVerifier-portable.zip" | Set-Content $zipSha

$setupArtifact = Join-Path $artifactDir "MediaVerifier-Setup.exe"
$setupSha = Join-Path $artifactDir "MediaVerifier-Setup.exe.sha256"
$setupManifest = Join-Path $artifactDir "MediaVerifier-Setup.exe.manifest.json"
$setupManifestSig = Join-Path $artifactDir "MediaVerifier-Setup.exe.manifest.sig"
Remove-Item $setupManifest, $setupManifestSig -Force -ErrorAction SilentlyContinue

$iscc = Get-InnoCompiler
if ($iscc) {
    & $iscc "/DAppVersion=$newVersion" "/DSourceDir=$appDir" "/DOutputDir=$artifactDir" $innoScript
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed"
    }

    $setupHash = (Get-FileHash $setupArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  MediaVerifier-Setup.exe" | Set-Content $setupSha
    Write-SignedUpdateManifest -Version $newVersion -SetupPath $setupArtifact -ManifestPath $setupManifest -SignaturePath $setupManifestSig
}
elseif ($env:GITHUB_ACTIONS -eq "true") {
    throw "Inno Setup 6 is required to build MediaVerifier-Setup.exe."
}
else {
    Write-Warning "Inno Setup 6 not found; skipped MediaVerifier-Setup.exe."
    Remove-Item $setupArtifact, $setupSha, $setupManifest, $setupManifestSig -Force -ErrorAction SilentlyContinue
}

Write-Host "Published $appDir and $zip ($newVersion)" -ForegroundColor Green
