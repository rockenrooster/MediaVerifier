param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "MediaVerifier.Calibration"),
    [string]$Ffmpeg = "ffmpeg"
)

# Downloads the real-material calibration corpus (subplan 15, Calibration, corpus b) into a cache outside the
# repository: segments of two freely licensed films (FFmpeg reads only the bytes it needs, through HTTP ranges),
# camera photos from NASA's image library and public-domain recordings from the Musopen collection. Photos and
# recordings are checked against pinned hashes (Musopen's published MD5s). Writes calibration.json, the labelled manifest that
# RealMaterialCalibration reads through MEDIAVERIFIER_CALIBRATION_DIR (an owner's own folder, corpus c, uses the
# same format). Nothing here is committed, and the app itself never downloads anything: this is a developer tool.
#
#   .\scripts\Get-CalibrationMedia.ps1
#   $env:MEDIAVERIFIER_CALIBRATION_DIR = "$env:LOCALAPPDATA\MediaVerifier.Calibration"
#   dotnet test -c Release --filter FullyQualifiedName~RealMaterialCalibration
#
# Needs the tooling FFmpeg (a full build with network protocols) on PATH, or -Ffmpeg pointing at one.

$ErrorActionPreference = "Stop"
$userAgent = "calibration-download/1.0"

$sintel = "https://download.blender.org/durian/movies/Sintel.2010.1080p.mkv"
$sintelLicence = "CC BY 3.0, (c) copyright Blender Foundation | durian.blender.org"
$tears = "https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov"
$tearsLicence = "CC BY 3.0, (CC) Blender Foundation | mango.blender.org"
$videos = @(
    @{ Name = "sintel-0105.mkv"; Url = $sintel; Start = 105; Licence = $sintelLicence },
    @{ Name = "sintel-0330.mkv"; Url = $sintel; Start = 330; Licence = $sintelLicence },
    @{ Name = "sintel-0600.mkv"; Url = $sintel; Start = 600; Licence = $sintelLicence },
    @{ Name = "tears-0120.mkv"; Url = $tears; Start = 120; Licence = $tearsLicence },
    @{ Name = "tears-0300.mkv"; Url = $tears; Start = 300; Licence = $tearsLicence },
    @{ Name = "tears-0540.mkv"; Url = $tears; Start = 540; Licence = $tearsLicence }
)
$segmentSeconds = 12

$nasa = "https://images-assets.nasa.gov/image/"
$nasaLicence = "Public domain, NASA (NASA media usage guidelines)"
# NASA publishes no hashes: these are the SHA-256 of the files as first downloaded on 2026-11-17.
$photos = @(
    @{ Name = "photo-01-wildlife.jpg"; Url = $nasa + "KSC-20210121-PH-JBS01_0079/KSC-20210121-PH-JBS01_0079~orig.jpg"; Sha256 = "49335e8b1d52ec056b222d1c641bc72495f1dc6b75c2cba018d0125f8db8c826" },
    @{ Name = "photo-02-flowers.jpg"; Url = $nasa + "KSC-20230613-PH-FMX01-0197/KSC-20230613-PH-FMX01-0197~orig.JPG"; Sha256 = "5846edc336a465f1c30cbaaf85579053f7131efb2d961ae4b062bd2bf83afd49" },
    @{ Name = "photo-03-heron.jpg"; Url = $nasa + "KSC-07PD-0357/KSC-07PD-0357~orig.jpg"; Sha256 = "b5a9c45994f5d132a5935887c91c90df6769296b6e7f26330e0ad708f6293c2d" },
    @{ Name = "photo-04-birds.jpg"; Url = $nasa + "KSC-07PD-0355/KSC-07PD-0355~orig.jpg"; Sha256 = "b22a9b8721f983bf650e65ae652e1934813035f3f91d0a6bb5b7cc6ea8998831" },
    @{ Name = "photo-05-hangar-people.jpg"; Url = $nasa + "AFRC2019-0134-12/AFRC2019-0134-12~orig.jpg"; Sha256 = "44d4fed3d27d084e854cf91132423dda366ce65696406e6b224d6bf9e7010353" },
    @{ Name = "photo-06-hangar.jpg"; Url = $nasa + "AFRC2019-0134-03/AFRC2019-0134-03~orig.jpg"; Sha256 = "89983e58dfa3f39d1f58cb5dd7fbf3effb7c0e57afb0f6f32894e4d12311279e" },
    @{ Name = "photo-07-portrait.jpg"; Url = $nasa + "jsc2025e068207_alt/jsc2025e068207_alt~orig.jpg"; Sha256 = "107a9a03a1a1114a082382a3d505bbeee3d4ed0cc0dd89aabad405ea586b7dd9" },
    @{ Name = "photo-08-portrait.jpg"; Url = $nasa + "jsc2025e078605_alt/jsc2025e078605_alt~orig.jpg"; Sha256 = "c556944d26b1f023ef2aae4ebaff4f07361527459b2db6fda9527bdfbc70d2e9" },
    @{ Name = "photo-09-campus.jpg"; Url = $nasa + "ACD16-0169-028/ACD16-0169-028~orig.jpg"; Sha256 = "7990a15483fa5d4b046263b37339704a5234a13b80bdc2f355abba4c80fce489" }
)

$musopen = "https://archive.org/download/MusopenCollectionAsFlac/"
$musopenLicence = "Public Domain Mark 1.0 (Musopen)"
$recordings = @(
    @{ Name = "audio-01-piano-aria.flac"; Url = $musopen + "Bach_GoldbergVariations/JohannSebastianBach-01-GoldbergVariationsBwv.988-Aria.flac"; Md5 = "853f9f9396946c3842ccbbcecf55a73d" },
    @{ Name = "audio-02-piano-variation.flac"; Url = $musopen + "Bach_GoldbergVariations/JohannSebastianBach-21-GoldbergVariationsBwv.988-Variation20.flac"; Md5 = "0c01473fdb20849ccce4b9fb3a3cfc81" },
    @{ Name = "audio-03-orchestra-grieg.flac"; Url = $musopen + "Greig_PeerGynt/EdvardGrieg-PeerGyntSuiteNo.1Op.46-04-InTheHallOfTheMountainKing.flac"; Md5 = "4655be52db91a64bac7b575f4124e9e4" },
    @{ Name = "audio-04-strings-suk.flac"; Url = $musopen + "Suk_Meditation/JosefSuk-Meditation.flac"; Md5 = "7e1f394d084bf3ec79e7f6fd80e1fddf" },
    @{ Name = "audio-05-orchestra-beethoven.flac"; Url = $musopen + "Beethoven_SymphonyNo.3Eroica/LudwigVanBeethoven-SymphonyNo.3InEFlatMajorEroicaOp.55-03-ScherzoAllegroVivace.flac"; Md5 = "98d695bb16028a5889873eba3a0ba872" }
)

function Get-Verified([string]$Url, [string]$Path, [string]$Algorithm, [string]$Expected) {
    if ((Test-Path $Path) -and (Get-FileHash $Path -Algorithm $Algorithm).Hash -eq $Expected.ToUpperInvariant()) {
        return
    }

    $partial = "$Path.partial"
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $partial -UserAgent $userAgent -UseBasicParsing
            break
        }
        catch {
            # Hosts answer 429 to downloads in quick succession: wait longer each time, then give up.
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -ne 429 -or $attempt -ge 6) {
                throw
            }

            Start-Sleep -Seconds (30 * $attempt)
        }
    }

    Start-Sleep -Seconds 5
    $actual = (Get-FileHash $partial -Algorithm $Algorithm).Hash
    if ($actual -ne $Expected.ToUpperInvariant()) {
        Remove-Item $partial -Force
        throw "$(Split-Path $Path -Leaf): $Algorithm $actual, expected $Expected"
    }

    Move-Item $partial $Path -Force
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$manifest = [System.Collections.Generic.List[object]]::new()

foreach ($video in $videos) {
    $path = Join-Path $Destination $video.Name
    if (-not (Test-Path $path)) {
        $partial = "$path.partial.mkv"
        & $Ffmpeg -hide_banner -nostdin -v error -y -ss $video.Start -i $video.Url -t $segmentSeconds -map 0:v:0 -c copy -f matroska $partial
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $partial)) {
            throw "Could not read $($segmentSeconds) s of $($video.Url) at $($video.Start) s"
        }

        Move-Item $partial $path -Force
    }

    $manifest.Add([ordered]@{ name = $video.Name; kind = "video"; label = "native"; source = "$($video.Url) from $($video.Start) s, $segmentSeconds s, stream copy"; licence = $video.Licence })
    Write-Host "Video  $($video.Name)"
}

foreach ($photo in $photos) {
    Get-Verified $photo.Url (Join-Path $Destination $photo.Name) "SHA256" $photo.Sha256
    $manifest.Add([ordered]@{ name = $photo.Name; kind = "photo"; label = "native"; source = $photo.Url; licence = $nasaLicence })
    Write-Host "Photo  $($photo.Name)"
}

foreach ($recording in $recordings) {
    Get-Verified $recording.Url (Join-Path $Destination $recording.Name) "MD5" $recording.Md5
    $manifest.Add([ordered]@{ name = $recording.Name; kind = "audio"; label = "lossless"; source = $recording.Url; licence = $musopenLicence })
    Write-Host "Audio  $($recording.Name)"
}

$json = [ordered]@{ comment = "Written by scripts/Get-CalibrationMedia.ps1 (subplan 15, corpus b)."; files = $manifest } | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $Destination "calibration.json"), $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "Calibration corpus ready in $Destination ($($manifest.Count) files)" -ForegroundColor Green
