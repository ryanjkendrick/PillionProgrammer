#Requires -Version 7.2
<#
.SYNOPSIS
    Runs Pillion's Visual Studio integration tests against the Experimental Instance of one Visual Studio.

.DESCRIPTION
    Runs the integration tests with that Visual Studio's vstest.console.exe: the build in
    tests/Pillion.VisualStudio.IntegrationTests for Visual Studio 2026 (18.x), or the one in
    tests/Pillion.VisualStudio.IntegrationTests.VS2022 for Visual Studio 2022 (the test framework has no version that
    drives both). Filtered to the version under test (test display names end with "(VS18)" or "(VS2022)").
    The test framework starts devenv /rootsuffix Exp of that installation (pinned through
    __UNITTESTEXPLORER_VSINSTALLPATH__), runs the tests inside it and closes it at the end.

    Before the run: checks that Pillion is deployed to that Experimental Instance (tools/vs-tests/Install-ToExp.ps1)
    and that no Experimental Instance of that Visual Studio is open, and backs up
    %LOCALAPPDATA%\PillionProgrammer\settings.json. After the run: restores settings.json byte for byte, copies the
    Activity Logs and Pillion's logs into the results folder, and checks that no Experimental Instance or
    Pillion.Host.exe started by the run is left (any that is left is reported and stopped, and the run fails).

.PARAMETER DevEnvPath
    The devenv.exe of the Visual Studio to test. Default: the newest installed Visual Studio.

.PARAMETER TestAssembly
    The built test assembly. Default: bin\Release\net48\Pillion.VisualStudio.IntegrationTests.dll of
    tests\Pillion.VisualStudio.IntegrationTests (18.x) or tests\Pillion.VisualStudio.IntegrationTests.VS2022 (17.x).

.PARAMETER ResultsDirectory
    Where the TRX file, logs and failure screenshots go. Default: TestResults\ide-<version> under the repository root.

.PARAMETER TestCaseFilter
    An extra vstest filter, combined with the version filter (for example "FullyQualifiedName~KillingTheHost").

.EXAMPLE
    pwsh tools/vs-tests/Invoke-IdeTests.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
#>
[CmdletBinding()]
param(
    [string]$DevEnvPath,
    [string]$TestAssembly,
    [string]$ResultsDirectory,
    [string]$TestCaseFilter
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VsTests.psm1') -Force
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$vs = Find-TargetVisualStudio -DevEnvPath $DevEnvPath
if (-not $TestAssembly) {
    $project = $vs.Major -eq 17 ? 'Pillion.VisualStudio.IntegrationTests.VS2022' : 'Pillion.VisualStudio.IntegrationTests'
    $TestAssembly = Join-Path $repoRoot "tests\$project\bin\Release\net48\Pillion.VisualStudio.IntegrationTests.dll"
}
if (-not (Test-Path -LiteralPath $TestAssembly)) { throw "No test assembly at $TestAssembly. Build the solution first (see README.md)." }
$TestAssembly = (Resolve-Path -LiteralPath $TestAssembly).Path
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repoRoot "TestResults\ide-$($vs.TestVersionName)" }
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$ResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path
Write-Host "Testing $($vs.DisplayName) ($($vs.DevEnv)), test cases ($($vs.TestVersionName)); results in $ResultsDirectory"

$deployed = Find-DeployedPillion -ExpHive $vs.ExpHive
if (-not $deployed) { throw "Pillion is not deployed to $($vs.ExpHive). Run tools/vs-tests/Install-ToExp.ps1 -DevEnvPath `"$($vs.DevEnv)`" first." }
Write-Host "Pillion deployed at $($deployed.Directory)"
$running = Get-ExpInstanceProcess -VisualStudio $vs
if ($running.Count -gt 0) { throw "An Experimental Instance of $($vs.DisplayName) is running (pid $($running.ProcessId -join ', ')). Close it first; the tests start their own." }

$vstest = $vs.VsTestConsole
if (-not (Test-Path -LiteralPath $vstest)) { throw "vstest.console.exe not found at $vstest (install the testing tools with Visual Studio)." }

$hostsBefore = @(Get-PillionHostProcess | ForEach-Object ProcessId)
$started = Get-Date
$backup = Backup-PillionSettings
$filter = "DisplayName~$($vs.TestVersionName)"
if ($TestCaseFilter) { $filter = "($filter)&($TestCaseFilter)" }
$trxName = "ide-$($vs.TestVersionName).trx"
$exitCode = 1
$saved = @{}
foreach ($name in '__UNITTESTEXPLORER_VSINSTALLPATH__', 'XUNIT_LOGS') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    # Pins the installation (not just the major version) the framework launches, and where it saves failure logs.
    [Environment]::SetEnvironmentVariable('__UNITTESTEXPLORER_VSINSTALLPATH__', (Join-Path $vs.Path 'Common7\IDE'))
    [Environment]::SetEnvironmentVariable('XUNIT_LOGS', (Join-Path $ResultsDirectory 'logs'))

    $arguments = @($TestAssembly, "/TestCaseFilter:$filter", "/logger:trx;LogFileName=$trxName", '/logger:console;verbosity=normal', "/ResultsDirectory:$ResultsDirectory")
    Write-Host "$vstest $($arguments -join ' ')"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $vstest @arguments
    $exitCode = $LASTEXITCODE
    Write-Host ("vstest.console exit {0} after {1:0.0} s" -f $exitCode, $clock.Elapsed.TotalSeconds)
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }

    # The framework closes the instance it started; anything still open from this run is a failure to report.
    $left = @(Get-ExpInstanceProcess -VisualStudio $vs | Where-Object { $_.CreationDate -ge $started })
    foreach ($p in $left) {
        Write-Warning "Experimental Instance pid $($p.ProcessId) is still running after the tests; stopping it."
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        $exitCode = $exitCode -ne 0 ? $exitCode : 1
    }

    # Pillion Host exits with its devenv.exe (job object); allow it a moment.
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $survivors = @(Get-PillionHostProcess | Where-Object { $hostsBefore -notcontains $_.ProcessId })
        if ($survivors.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    foreach ($s in $survivors) {
        Write-Warning "Pillion.Host.exe pid $($s.ProcessId) (parent $($s.ParentProcessId)) outlived the run; stopping it."
        Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue
        $exitCode = $exitCode -ne 0 ? $exitCode : 1
    }
    if ($survivors.Count -eq 0) { Write-Host 'No Pillion.Host.exe left from this run.' }

    Restore-PillionSettings -Backup $backup

    $logs = Join-Path $ResultsDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $activityLog = Join-Path $vs.ExpRoamingHive 'ActivityLog.xml'
    if (Test-Path -LiteralPath $activityLog) { Copy-Item -LiteralPath $activityLog -Destination (Join-Path $logs "ActivityLog-file-VS$($vs.Major).xml") -Force }
    $pillionLogs = Join-Path $env:LOCALAPPDATA 'PillionProgrammer\logs'
    if (Test-Path -LiteralPath $pillionLogs) {
        Get-ChildItem -LiteralPath $pillionLogs -File | Where-Object { $_.LastWriteTime -ge $started } |
            Copy-Item -Destination $logs -Force
    }
}

exit $exitCode
