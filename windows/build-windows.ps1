$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\DungeonMasterAI.App\DungeonMasterAI.App.csproj'
$smoke = Join-Path $root 'tests\DungeonMasterAI.Smoke\DungeonMasterAI.Smoke.csproj'
$sample = Join-Path (Split-Path -Parent $root) 'reference-python\demo\sample_campaign_manifest.json'
$out = Join-Path $root 'artifacts\DungeonMasterAI-win-x64'

dotnet restore $project
dotnet run --project $smoke --configuration Release -- $sample
dotnet build $project --configuration Release --no-restore
dotnet publish $project --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $out
Write-Host "Build complete: $out"
