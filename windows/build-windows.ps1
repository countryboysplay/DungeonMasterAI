$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\DungeonMasterAI.App\DungeonMasterAI.App.csproj'
$smoke = Join-Path $root 'tests\DungeonMasterAI.Smoke\DungeonMasterAI.Smoke.csproj'
$rollTests = Join-Path $root 'tests\DungeonMasterAI.RollTests\DungeonMasterAI.RollTests.csproj'
$provisioningTests = Join-Path $root 'tests\DungeonMasterAI.RuntimeProvisioningTests\DungeonMasterAI.RuntimeProvisioningTests.csproj'
$fetchRuntime = Join-Path $root 'tools\fetch-llama-runtime.ps1'
$sample = Join-Path (Split-Path -Parent $root) 'reference-python\demo\sample_campaign_manifest.json'
$out = Join-Path $root 'artifacts\DungeonMasterAI-win-x64'

# Vendor the pinned llama.cpp runtime into src/DungeonMasterAI.App/Runtime BEFORE publish. The App
# project copies Runtime/**/* to the output and the installer packages the whole publish tree, so
# skipping this produces a setup that installs cleanly and then reports "Runtime incomplete" on
# every launch. The script is idempotent and re-downloads nothing when the files are already there.
# It reports failure by throwing, not through an exit code, and $ErrorActionPreference is Stop.
& $fetchRuntime

dotnet restore $project
dotnet run --project $rollTests --configuration Release
dotnet run --project $smoke --configuration Release -- $sample
# Guards the provisioning contract this build depends on: the embedded pins, the runtime readiness
# check, and the resumable hash-verified downloader that fetches the model on first run.
dotnet run --project $provisioningTests --configuration Release
dotnet build $project --configuration Release --no-restore
dotnet publish $project --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $out

# The installer must never ship a runtime directory that the app will reject at launch.
$publishedRuntime = Join-Path $out 'Runtime'
if (-not (Test-Path -LiteralPath (Join-Path $publishedRuntime 'llama-server-impl.dll'))) {
    throw "The published output has no usable llama.cpp runtime at $publishedRuntime."
}

Write-Host "Build complete: $out"
