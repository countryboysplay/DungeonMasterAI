#Requires -Version 7.0
<#
.SYNOPSIS
    Vendors the pinned llama.cpp Windows CPU runtime into src/DungeonMasterAI.App/Runtime.

.DESCRIPTION
    Build-time / CI tool. This never runs on a user's machine: the app ships the result of this
    script inside the installer. It downloads the exact asset named in runtime.lock.json, verifies
    its byte size and SHA-256 against the lock, extracts it, and copies out ONLY the files the
    llama-server host actually needs.

    The subset matters. The release zip is 51 flat files, ~46.7 MB extracted, and most of it is
    other CLI tools (llama-cli, llama-bench, llama-tts, llama-imatrix, llama-quantize, the
    ggml-rpc-server, the mtmd/llava/qwen2vl CLIs). Shipping those would grow the installer and
    would put an RPC server and a set of general-purpose inference CLIs inside the install
    directory for no benefit.

    Note also that llama-server.exe is a 9,216-byte stub launcher: the real server lives in
    llama-server-impl.dll. Copying the exe alone produces an install that looks complete and
    fails on every launch, which is why the file list is explicit and every entry is required.

.PARAMETER Tag
    Overrides the pinned tag. Use with -UpdateLock to re-pin.

.PARAMETER UpdateLock
    Re-reads the GitHub release API for the resolved tag and rewrites the url, sizeBytes and
    sha256 fields of runtime.lock.json from the published per-asset digest. The digest is
    mandatory: if GitHub does not publish one for the asset, this fails rather than pinning a
    size-only check on an executable we are about to ship.

.PARAMETER Force
    Re-vendors even when the destination already satisfies the manifest.
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [switch]$UpdateLock,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$windowsRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $windowsRoot 'runtime.lock.json'
$destination = Join-Path $windowsRoot 'src/DungeonMasterAI.App/Runtime'

if (-not (Test-Path $lockPath)) { throw "runtime.lock.json was not found at $lockPath." }
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json

$resolvedTag = if ($Tag) { $Tag } else { $lock.tag }
$assetName = "llama-$resolvedTag-bin-win-cpu-x64.zip"

function Get-ReleaseAsset {
    param([string]$ReleaseTag, [string]$AssetName)

    $headers = @{ 'User-Agent' = 'DungeonMasterAI-fetch-llama-runtime'; 'Accept' = 'application/vnd.github+json' }
    # GITHUB_TOKEN lifts the 60/hour unauthenticated limit on hosted runners. Optional locally.
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$($lock.repository)/releases/tags/$ReleaseTag" -Headers $headers
    # Match the asset name exactly. The old runtime code filtered on the substring "win-x64",
    # which matches nothing at all today: the asset is "win-cpu-x64".
    $asset = $release.assets | Where-Object { $_.name -eq $AssetName }
    if (-not $asset) {
        throw "Release $ReleaseTag does not publish $AssetName. Assets: $(($release.assets | ForEach-Object { $_.name }) -join ', ')"
    }
    return $asset
}

if ($UpdateLock) {
    Write-Host "Re-pinning runtime.lock.json to $resolvedTag..."
    $asset = Get-ReleaseAsset -ReleaseTag $resolvedTag -AssetName $assetName
    if (-not $asset.digest -or -not $asset.digest.StartsWith('sha256:')) {
        throw "GitHub published no sha256 digest for $assetName. Refusing to pin a runtime executable on a size-only check."
    }
    $lock.tag = $resolvedTag
    $lock.asset = $asset.name
    $lock.url = $asset.browser_download_url
    $lock.sizeBytes = [int64]$asset.size
    $lock.sha256 = $asset.digest.Substring('sha256:'.Length).ToLowerInvariant()
    $lock | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $lockPath -Encoding utf8NoBOM
    Write-Host "Pinned $($lock.asset) ($($lock.sizeBytes) bytes, sha256 $($lock.sha256))."
}

if ($resolvedTag -ne $lock.tag) {
    throw "Tag '$resolvedTag' does not match the pinned tag '$($lock.tag)'. Re-run with -UpdateLock to re-pin deliberately."
}

# Idempotence: CI reruns and local rebuilds should not re-download 18 MB for nothing.
$alreadyVendored = $true
foreach ($file in $lock.files) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $file))) { $alreadyVendored = $false; break }
}
if ($alreadyVendored -and -not $Force) {
    Write-Host "Runtime $($lock.tag) is already vendored in $destination ($($lock.files.Count) files). Use -Force to re-vendor."
    return
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "dmai-llama-runtime-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $archive = Join-Path $staging $lock.asset
    Write-Host "Downloading $($lock.url)..."
    Invoke-WebRequest -Uri $lock.url -OutFile $archive -UseBasicParsing -Headers @{ 'User-Agent' = 'DungeonMasterAI-fetch-llama-runtime' }

    $actualSize = (Get-Item -LiteralPath $archive).Length
    if ($actualSize -ne $lock.sizeBytes) {
        throw "Size mismatch for $($lock.asset): got $actualSize bytes, lock expects $($lock.sizeBytes)."
    }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $lock.sha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $($lock.asset): got $actualHash, lock expects $($lock.sha256)."
    }
    Write-Host "Verified $($lock.asset): $actualSize bytes, sha256 $actualHash."

    $extract = Join-Path $staging 'extract'
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $copied = 0
    $bytes = 0L
    foreach ($file in $lock.files) {
        # The zip is flat today, but resolve recursively so a future layout change fails loudly
        # on a missing file rather than silently vendoring an incomplete runtime.
        $source = Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $file | Select-Object -First 1
        if (-not $source) {
            throw "Required runtime file '$file' was not present in $($lock.asset). The pinned build's layout changed; re-pin deliberately."
        }
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $destination $file) -Force
        $copied++
        $bytes += $source.Length
    }

    Set-Content -LiteralPath (Join-Path $destination 'runtime-version.txt') -Value $lock.tag -Encoding utf8NoBOM -NoNewline

    # The readiness contract the app enforces at runtime, asserted here too so a broken vendor
    # step fails in CI instead of shipping an install that reports "Runtime installed" and
    # then fails on every launch.
    foreach ($required in @('llama-server.exe', 'llama-server-impl.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Vendored runtime is missing $required." }
    }
    if (-not (Get-ChildItem -LiteralPath $destination -Filter 'ggml-cpu-*.dll')) {
        throw 'Vendored runtime contains no ggml-cpu-*.dll backend.'
    }

    Write-Host "Vendored llama.cpp $($lock.tag) into $destination ($copied files, $([math]::Round($bytes / 1MB, 1)) MB)."
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
