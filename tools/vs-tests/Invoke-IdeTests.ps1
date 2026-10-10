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
    Pillion.Host.exe of the run is left (any that is left is reported and stopped, and the run fails). A host counts as the run's own only when its parent process is an Experimental Instance this run started (the parent chain is walked); a host of another run, or one whose parent cannot be determined, is logged and left alone.

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
# Pillion.Host.exe processes are told apart by their parent: only a host whose parent is an Experimental Instance this run
# started is this run's to stop. The instance is gone by the time the tests end, so its process id is noted while it runs.
$ownInstances = [hashtable]::Synchronized(@{})
$ownInstanceWatcher = Start-ThreadJob -Name 'ide-own-instances' -ScriptBlock {
    param($table, $modulePath, $devEnv, $startedAt)
    Import-Module $modulePath -Force
    $visualStudio = [pscustomobject]@{ DevEnv = $devEnv }
    while ($true) {
        foreach ($p in @(Get-ExpInstanceProcess -VisualStudio $visualStudio)) {
            if ($p.CreationDate -ge $startedAt) { $table[[int]$p.ProcessId] = $true }
        }
        Start-Sleep -Milliseconds 250
    }
} -ArgumentList $ownInstances, (Join-Path $PSScriptRoot 'VsTests.psm1'), $vs.DevEnv, (Get-Date)
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

    # The console logger shows a test's output only when it fails; the host kill test's measurements (UI thread and
    # background thread gaps) are worth seeing on every run, so print them from the TRX file.
    try {
        $trxPath = Join-Path $ResultsDirectory $trxName
        if (Test-Path -LiteralPath $trxPath) {
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
            foreach ($result in @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*KillingTheHost*' })) {
                Write-Host "Output of $($result.testName) ($($result.outcome)):"
                $lines = @($result.Output.StdOut -split "`r?`n" | Where-Object { $_ -match '^(Killed|\s+\d+ ms: |UI thread|Background thread|WARNING)' })
                $lines | ForEach-Object { Write-Host "  $_" }
                if ($env:GITHUB_ACTIONS -eq 'true') {
                    foreach ($line in @($lines | Where-Object { $_ -like 'WARNING*' })) { Write-Host "::warning title=Host kill test::$line" }
                }
            }
        }
    }
    catch { Write-Warning "Could not read the test output from ${trxName}: $($_.Exception.Message)" }
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

    Stop-Job $ownInstanceWatcher -ErrorAction SilentlyContinue
    Remove-Job $ownInstanceWatcher -Force -ErrorAction SilentlyContinue
    $ownPids = @($ownInstances.Keys | ForEach-Object { [int]$_ })

    # Pillion Host exits with its devenv.exe (job object); allow it a moment. Another run's host (another agent testing at
    # the same time, or a Visual Studio the developer is using) has a different parent and is never touched; a host whose
    # parent cannot be determined is only reported.
    function Get-HostOwnership($hostProcess) {
        $parent = [int]$hostProcess.ParentProcessId
        if ($parent -le 0) { return 'unknown' }
        $chain = @($parent) + @(Get-AncestorProcessId -ProcessId $parent)
        if (@($chain | Where-Object { $ownPids -contains $_ }).Count -gt 0) { return 'own' }
        return 'other'
    }
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $candidates = @(Get-PillionHostProcess | Where-Object { $hostsBefore -notcontains $_.ProcessId })
        $survivors = @($candidates | Where-Object { (Get-HostOwnership $_) -eq 'own' })
        if ($survivors.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    foreach ($s in $survivors) {
        Write-Warning "Pillion.Host.exe pid $($s.ProcessId) (parent $($s.ParentProcessId), an Experimental Instance of this run) outlived the run; stopping it."
        Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue
        $exitCode = $exitCode -ne 0 ? $exitCode : 1
    }
    foreach ($c in @($candidates | Where-Object { $survivors.ProcessId -notcontains $_.ProcessId })) {
        if ((Get-HostOwnership $c) -eq 'unknown') {
            Write-Warning "Pillion.Host.exe pid $($c.ProcessId) is new since the run started but its parent cannot be determined; not stopping it."
        }
        else {
            Write-Host "Pillion.Host.exe pid $($c.ProcessId) (parent $($c.ParentProcessId)) is not from this run; left alone."
        }
    }
    if ($survivors.Count -eq 0) { Write-Host 'No Pillion.Host.exe from this run is left.' }

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
