param(
    [switch]$LaunchApp
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_XMLDOC_MODE = 'skip'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $root
$appProject = Join-Path $root 'src\DungeonMasterAI.App\DungeonMasterAI.App.csproj'
$smokeProject = Join-Path $root 'tests\DungeonMasterAI.Smoke\DungeonMasterAI.Smoke.csproj'
$sampleCampaign = Join-Path $projectRoot 'reference-python\demo\sample_campaign_manifest.json'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultsRoot = Join-Path $root "test-results\$timestamp"
$publishDir = Join-Path $resultsRoot 'publish'
$summaryPath = Join-Path $resultsRoot 'SUMMARY.txt'
$resultZip = Join-Path $root "DungeonMasterAI-TestResults-$timestamp.zip"

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

function Write-Summary([string]$Text) {
    $Text | Tee-Object -FilePath $summaryPath -Append | Out-Host
}

function Invoke-LoggedStep {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][scriptblock]$Command
    )

    $logPath = Join-Path $resultsRoot ("{0}.log" -f $Name)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan

    # Windows PowerShell 5.1 can promote text written by native applications to
    # stderr into a terminating NativeCommandError when the caller uses
    # $ErrorActionPreference = 'Stop'. dotnet legitimately writes some
    # diagnostics to stderr, so temporarily allow native stderr to flow into
    # the log and use the process exit code as the authoritative result.
    $oldPreference = $ErrorActionPreference
    $exitCode = -1
    try {
        $ErrorActionPreference = 'Continue'
        & $Command 2>&1 | Tee-Object -FilePath $logPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. See $logPath"
    }
}

$passed = $false
try {
    Write-Summary "Dungeon Master AI Windows test run"
    Write-Summary "Started: $(Get-Date -Format o)"
    Write-Summary "Computer: $env:COMPUTERNAME"
    Write-Summary "Windows: $([System.Environment]::OSVersion.VersionString)"

    try {
        Get-CimInstance Win32_OperatingSystem |
            Select-Object Caption, Version, BuildNumber, OSArchitecture |
            Format-List | Out-File (Join-Path $resultsRoot 'windows-info.txt')
        Get-CimInstance Win32_Processor |
            Select-Object Name, NumberOfCores, NumberOfLogicalProcessors |
            Format-List | Out-File (Join-Path $resultsRoot 'cpu-info.txt')
        Get-CimInstance Win32_VideoController |
            Select-Object Name, DriverVersion, AdapterRAM |
            Format-List | Out-File (Join-Path $resultsRoot 'gpu-info.txt')
    }
    catch {
        "System inventory collection warning: $($_.Exception.Message)" | Out-File (Join-Path $resultsRoot 'inventory-warning.txt')
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK is not installed or dotnet.exe is not on PATH. Install .NET 10 SDK and run this script again.'
    }

    $dotnetVersion = (& dotnet --version).Trim()
    Write-Summary ".NET SDK: $dotnetVersion"
    $majorText = ($dotnetVersion -split '\.')[0]
    $major = 0
    [void][int]::TryParse($majorText, [ref]$major)
    if ($major -lt 10) {
        throw "This source targets .NET 10. Found SDK $dotnetVersion. Install .NET 10 SDK and run again."
    }

    Invoke-LoggedStep '01-dotnet-info' { dotnet --info }
    Invoke-LoggedStep '02-restore-app' { dotnet restore $appProject }
    Invoke-LoggedStep '03-restore-smoke' { dotnet restore $smokeProject }
    Invoke-LoggedStep '04-build-app' { dotnet build $appProject --configuration Release --no-restore }
    Invoke-LoggedStep '05-smoke-tests' { dotnet run --project $smokeProject --configuration Release --no-restore -- $sampleCampaign }
    Invoke-LoggedStep '06-publish-win-x64' {
        dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $publishDir
    }

    $exe = Join-Path $publishDir 'DungeonMasterAI.exe'
    if (-not (Test-Path $exe)) {
        throw "Publish completed but $exe was not found."
    }

    # Keep a clean runnable copy beside the test-results folder. Launch from this
    # copy so the published files inside the results package remain unlocked and
    # Compress-Archive can always finish even while the GUI is still open.
    $readyDir = Join-Path $root 'READY-TO-RUN-APP'
    if (Test-Path $readyDir) { Remove-Item $readyDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $readyDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $readyDir -Recurse -Force
    $readyExe = Join-Path $readyDir 'DungeonMasterAI.exe'

    Write-Summary 'RESULT: PASS'
    Write-Summary "Published app: $exe"
    Write-Summary "Ready-to-run app: $readyExe"
    $passed = $true

    if ($LaunchApp) {
        $startupLog = Join-Path $env:LOCALAPPDATA 'DungeonMasterAI\Logs\startup.log'
        if (Test-Path $startupLog) { Remove-Item $startupLog -Force -ErrorAction SilentlyContinue }
        $launchStarted = Get-Date

        Write-Host "Launching $readyExe with startup diagnostics enabled" -ForegroundColor Green
        $appProcess = Start-Process -FilePath $readyExe -WorkingDirectory $readyDir -PassThru
        Start-Sleep -Seconds 5

        if (Test-Path $startupLog) {
            Copy-Item $startupLog (Join-Path $resultsRoot '07-gui-startup.log') -Force
        }
        else {
            'The application did not create its startup log.' | Out-File (Join-Path $resultsRoot '07-gui-startup.log')
        }

        if ($appProcess.HasExited) {
            Write-Summary "GUI LAUNCH: FAIL - process exited within 5 seconds with exit code $($appProcess.ExitCode)."
            try {
                Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$launchStarted.AddSeconds(-2) } -ErrorAction Stop |
                    Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') } |
                    Select-Object -First 30 TimeCreated, ProviderName, Id, LevelDisplayName, Message |
                    Format-List | Out-File (Join-Path $resultsRoot '08-gui-windows-events.txt')
            }
            catch {
                "Could not read Windows Application events: $($_.Exception.Message)" | Out-File (Join-Path $resultsRoot '08-gui-windows-events.txt')
            }
            throw "DungeonMasterAI.exe exited during the GUI launch check. See 07-gui-startup.log and 08-gui-windows-events.txt in the results package."
        }

        Write-Summary "GUI LAUNCH: process remained alive for the 5-second startup check."
        Write-Summary "Startup log: $startupLog"
        Write-Host "The application is still running from READY-TO-RUN-APP. Test the GUI normally; the results ZIP can now be created without locking errors." -ForegroundColor Green
    }
}
catch {
    Write-Summary 'RESULT: FAIL'
    $errorText = ($_ | Out-String).Trim()
    Write-Summary "ERROR: $($_.Exception.Message)"
    if ([string]::IsNullOrWhiteSpace($_.Exception.Message)) { Write-Summary "DETAIL: $errorText" }
    $errorText | Out-File (Join-Path $resultsRoot 'failure-details.txt')
}
finally {
    Write-Summary "Finished: $(Get-Date -Format o)"
    if (Test-Path $resultZip) { Remove-Item $resultZip -Force }
    Compress-Archive -Path (Join-Path $resultsRoot '*') -DestinationPath $resultZip -Force
    Write-Host "`nResults package: $resultZip" -ForegroundColor Yellow
}

if (-not $passed) { exit 1 }
exit 0
