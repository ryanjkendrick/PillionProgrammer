#Requires -Version 7.5
<#
.SYNOPSIS
    Helpers for tools/gate1/Invoke-Gate1.ps1: test runs and their TRX files, the IDE scenario run in the Experimental
    Instance, the extension-on against extension-off launches for the VSIX cost, model digests, CI runs, and the Gate 1
    criteria table. Uses tools/gate0/Gate0.Checks.psm1 (imported by the caller) for logging, settings and installation.

.DESCRIPTION
    Processes: everything started here is started by this module and stopped by it (by process id). A devenv.exe is
    never stopped unless this module launched it. Before any Visual Studio run (installing into a hive, an IDE test run,
    a launch) Wait-NoDevenv waits until no devenv.exe runs at all, because the Experimental Instance's hive is shared by
    every IDE test run on the machine (decision log, 2026-10-10).
#>

Set-StrictMode -Version 3.0

#region Processes and waiting

function Wait-NoDevenv {
    <# Waits until no devenv.exe is running (polling every 15 s), up to $TimeoutSec. Returns the seconds waited. #>
    param([int]$TimeoutSec = 3600, [string]$Why = 'the next Visual Studio step')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $announced = $false
    while ($true) {
        $running = @(Get-Process -Name devenv -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return [int]$clock.Elapsed.TotalSeconds }
        if (-not $announced) {
            Write-Gate0Log ("Waiting for devenv.exe (pid {0}) to exit before {1} (another agent or the developer may be using Visual Studio)." -f ($running.Id -join ', '), $Why) -Level warn
            $announced = $true
        }
        if ($clock.Elapsed.TotalSeconds -gt $TimeoutSec) { throw "devenv.exe (pid $($running.Id -join ', ')) was still running after $TimeoutSec s; $Why did not start." }
        Start-Sleep -Seconds 15
    }
}

function Invoke-ChildPwsh {
    <#
    Runs a PowerShell script in a child pwsh (so its exit does not end this script) with extra environment variables,
    writing its output to a log file. Returns the exit code.
    #>
    param([Parameter(Mandatory)][string]$Script, [string[]]$Arguments = @(), [hashtable]$Environment = @{}, [Parameter(Mandatory)][string]$LogPath)
    $saved = @{}
    foreach ($k in $Environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Environment[$k]) }
    try {
        & pwsh -NoProfile -File $Script @Arguments *>&1 | Out-File -LiteralPath $LogPath -Encoding utf8
        return $LASTEXITCODE
    }
    finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

#endregion

#region Test results

function Read-TrxResults {
    <# Every test result in a TRX file: name, outcome, duration, project (from the test's storage), stdout and error. #>
    param([Parameter(Mandatory)][string]$Path)
    [xml]$x = Get-Content -LiteralPath $Path -Raw
    $ns = [System.Xml.XmlNamespaceManager]::new($x.NameTable); $ns.AddNamespace('t', $x.DocumentElement.NamespaceURI)
    $storage = @{}
    foreach ($ut in $x.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) { $storage[$ut.id] = [IO.Path]::GetFileNameWithoutExtension($ut.storage) }
    foreach ($r in $x.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        $out = $r.SelectSingleNode('t:Output/t:StdOut', $ns)
        $msg = $r.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
        [pscustomobject]@{
            Name     = $r.testName
            Outcome  = $r.outcome
            Duration = $r.duration
            Project  = $(if ($storage.ContainsKey($r.testId)) { $storage[$r.testId] } else { [IO.Path]::GetFileNameWithoutExtension($Path) })
            StdOut   = $(if ($out) { $out.InnerText } else { '' })
            Error    = $(if ($msg) { $msg.InnerText } else { $null })
            Trx      = $Path
        }
    }
}

function Invoke-DotnetTestRun {
    <#
    dotnet test with TRX files into $ResultsDir; returns counts per project, totals, failed and skipped names, and every
    result (for reading the measurement lines tests print).
    #>
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string[]]$Targets, [Parameter(Mandatory)][string]$Filter, [Parameter(Mandatory)][string]$ResultsDir, [Parameter(Mandatory)][string]$LogPath, [switch]$NoBuild, [switch]$Serial)
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $codes = @()
    foreach ($target in $Targets) {
        $testArgs = @('test', $target, '-c', 'Release', '--filter', $Filter, '--logger', 'trx', '--results-directory', $ResultsDir)
        if ($NoBuild) { $testArgs += '--no-build' }
        # Live tests share one Ollama: classes running side by side would load models over each other's measurements.
        if ($Serial) { $testArgs += @('--', 'xUnit.ParallelizeTestCollections=false') }
        Push-Location $RepoRoot
        try { & dotnet @testArgs 2>&1 | Out-File -LiteralPath $LogPath -Encoding utf8 -Append; $codes += $LASTEXITCODE }
        finally { Pop-Location }
    }
    $results = @(Get-ChildItem -LiteralPath $ResultsDir -Filter '*.trx' -ErrorAction SilentlyContinue | ForEach-Object { Read-TrxResults -Path $_.FullName })
    $projects = [ordered]@{}
    foreach ($r in $results) {
        if (-not $projects.Contains($r.Project)) { $projects[$r.Project] = [ordered]@{ passed = 0; failed = 0; skipped = 0 } }
        switch ($r.Outcome) { 'Passed' { $projects[$r.Project].passed++ } 'Failed' { $projects[$r.Project].failed++ } default { $projects[$r.Project].skipped++ } }
    }
    $totals = [ordered]@{ passed = @($results | Where-Object Outcome -EQ 'Passed').Count; failed = @($results | Where-Object Outcome -EQ 'Failed').Count; skipped = @($results | Where-Object { $_.Outcome -notin 'Passed', 'Failed' }).Count }
    [pscustomobject]@{
        ExitCodes = $codes
        Projects  = $projects
        Totals    = $totals
        Failed    = @($results | Where-Object Outcome -EQ 'Failed' | ForEach-Object { "$($_.Name): $($_.Error)" })
        Skipped   = @($results | Where-Object { $_.Outcome -notin 'Passed', 'Failed' } | ForEach-Object { $_.Name })
        Results   = $results
    }
}

function Select-OutputLines {
    <# The stdout lines of the tests whose name matches $TestPattern that match $LinePattern. #>
    param([Parameter(Mandatory)][object[]]$Results, [Parameter(Mandatory)][string]$TestPattern, [string]$LinePattern = '.')
    foreach ($r in @($Results | Where-Object { $_.Name -match $TestPattern })) {
        foreach ($line in ($r.StdOut -split "`r?`n")) {
            if ($line -match $LinePattern -and $line -notmatch '^\d\d:\d\d:\d\d\.\d+ (info|dbug|warn|fail|trce):') { [pscustomobject]@{ Test = $r.Name; Outcome = $r.Outcome; Line = $line.Trim() } }
        }
    }
}

function Get-LiveMeasurements {
    <# Pulls the latency and cache figures the live and benchmark tests print into a dictionary (lines kept verbatim). #>
    param([object[]]$LiveResults = @(), [object[]]$UnitResults = @())
    $m = [ordered]@{}
    $pick = {
        param($results, $test, $pattern)
        @(Select-OutputLines -Results $results -TestPattern $test -LinePattern $pattern | ForEach-Object { $_.Line })
    }
    $m['autocomplete7bGpu'] = & $pick $LiveResults 'Latency_Large_OnTheGpu' 'first token p50|Steady typing|prompt-evaluation'
    $m['autocomplete15bGpu'] = & $pick $LiveResults 'Latency_Small_OnTheGpu' 'first token p50|Steady typing|prompt-evaluation'
    $m['autocomplete15bCpuBudget'] = & $pick $LiveResults 'Latency_Small_WithTheCpuBudget' 'first token p50|Steady typing'
    $m['autocomplete15bCpuForced'] = & $pick $LiveResults 'Latency_Small_OnTheCpu_Direct' 'first token p50'
    $m['prefixCache'] = & $pick $LiveResults 'SecondKeystroke' 'request|prompt'
    $m['inlineEdit'] = & $pick $LiveResults 'InlineEditLiveTests' ': first text p50'
    $m['residency'] = & $pick $LiveResults 'OllamaResidencyLiveTests' 'beside the pinned|needs 8,192|after the reload|NOT RUN'
    $m['contextAssembly'] = & $pick $UnitResults 'ContextAssemblyBenchmarkTests' 'assembly p50'
    $m['chatRendering'] = & $pick $UnitResults 'ChatRenderingTests' 'median'
    $m['degradationTest'] = @($UnitResults | Where-Object { $_.Name -match 'SlowHost_Degrades|P95OverOnePointFiveSeconds_Degrades|Degraded_UsesThe350msDebounceAndSingleLine|TheDegradationRule_ReadsTheSameWindow' } | ForEach-Object { "$($_.Name): $($_.Outcome)" })
    return $m
}

function Get-FirstMatchNumber {
    <# The first number captured by $Pattern (group 1) in any of the lines, or $null. #>
    param([string[]]$Lines, [Parameter(Mandatory)][string]$Pattern, [int]$Group = 1)
    foreach ($l in @($Lines)) { if ($l -match $Pattern) { return [double]($Matches[$Group] -replace ',', '') } }
    return $null
}

#endregion

#region IDE scenario run

function Invoke-GateIdeRun {
    <#
    Runs the Visual Studio integration tests (the whole suite, with the Gate 1 scenarios switched on) in the Experimental
    Instance of the driven Visual Studio through tools/vs-tests/Invoke-IdeTests.ps1, after installing this build into
    that instance when the deployed copy differs. Returns the TRX results, the scenario JSON files and what the package
    load and host-kill tests printed.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)]$VisualStudio,
        [Parameter(Mandatory)][string]$RunDir,
        [Parameter(Mandatory)][hashtable]$ScenarioEnvironment,
        [string]$TestCaseFilter,
        [int]$WaitForDevenvSec = 3600
    )
    $label = "VS$($VisualStudio.Major)"
    $resultsDir = Join-Path $RunDir "ide-$label"
    $scenarioDir = Join-Path $resultsDir 'scenarios'
    New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null
    $waited = Wait-NoDevenv -TimeoutSec $WaitForDevenvSec -Why "the IDE run on $label"

    # This build must be the one in the Experimental Instance (another agent may have installed theirs).
    $vsix = Join-Path $RepoRoot 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
    $built = Join-Path $RepoRoot 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.dll'
    $expHive = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$($VisualStudio.Major).0_$($VisualStudio.InstanceId)Exp"
    $deployed = Find-DeployedExtension -ExpHive $expHive
    $install = 'already deployed (same Pillion.VisualStudio.dll)'
    if (-not $deployed -or (Get-FileHash $built).Hash -ne (Get-FileHash $deployed.PackageDll).Hash) {
        $r = Install-VsixToExp -VisualStudio $VisualStudio -VsixPath $vsix -RootSuffix 'Exp'
        $install = $r.method
        $deployed = Find-DeployedExtension -ExpHive $expHive
        if (-not $deployed -or (Get-FileHash $built).Hash -ne (Get-FileHash $deployed.PackageDll).Hash) { throw "After installing, the Experimental Instance ($expHive) still does not hold this build's Pillion.VisualStudio.dll." }
    }
    $started = [DateTimeOffset]::Now
    $env = @{}
    foreach ($k in $ScenarioEnvironment.Keys) { $env[$k] = $ScenarioEnvironment[$k] }
    $env['PILLION_GATE_SCENARIOS'] = '1'
    $env['PILLION_GATE_OUT'] = $scenarioDir
    $arguments = @('-DevEnvPath', $VisualStudio.DevEnv, '-ResultsDirectory', $resultsDir)
    if ($TestCaseFilter) { $arguments += @('-TestCaseFilter', $TestCaseFilter) }
    $log = Join-Path $resultsDir 'invoke-idetests.log'
    $code = Invoke-ChildPwsh -Script (Join-Path $RepoRoot 'tools\vs-tests\Invoke-IdeTests.ps1') -Arguments $arguments -Environment $env -LogPath $log
    $trx = Get-ChildItem -LiteralPath $resultsDir -Filter '*.trx' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $results = if ($trx) { @(Read-TrxResults -Path $trx.FullName) } else { @() }
    $scenarios = [ordered]@{}
    foreach ($f in @(Get-ChildItem -LiteralPath $scenarioDir -Filter '*.json' -ErrorAction SilentlyContinue)) { $scenarios[$f.BaseName] = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -AsHashtable }
    $packageLoad = @(Select-OutputLines -Results $results -TestPattern 'PackageLoad_UsesLittleUiThreadCpu' -LinePattern '^UI thread CPU from' | ForEach-Object Line) | Select-Object -First 1
    $hostKill = @(Select-OutputLines -Results $results -TestPattern 'KillingTheHost' -LinePattern '^(Killed|UI thread|Background thread|WARNING)' | ForEach-Object Line)
    [pscustomobject]@{
        Label        = $label
        ExitCode     = $code
        WaitedForDevenvSec = $waited
        Install      = $install
        DeployedTo   = $deployed.Directory
        Started      = $started
        ResultsDir   = $resultsDir
        Trx          = $(if ($trx) { $trx.FullName } else { $null })
        Log          = $log
        Results      = $results
        Scenarios    = $scenarios
        PackageLoad  = $packageLoad
        HostKill     = $hostKill
    }
}

#endregion

#region VSIX cost: extension on against extension off

function New-CostFolder {
    <# The scratch folder both launches open (a few C# files). #>
    param([Parameter(Mandatory)][string]$Root)
    $folder = Join-Path $Root 'gate1-cost-folder'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    'namespace Cost; internal static class Program { private static void Main() { } }' | Set-Content -LiteralPath (Join-Path $folder 'Program.cs') -Encoding utf8
    'namespace Cost; internal sealed class Widget { public int Size { get; set; } }' | Set-Content -LiteralPath (Join-Path $folder 'Widget.cs') -Encoding utf8
    return $folder
}

function Measure-DevenvLaunch {
    <#
    Launches devenv /rootsuffix <suffix> on the folder, waits for the main window (its title names the folder), then
    reads the UI thread's CPU time (devenv's initial thread) $CpuAtSec after the window appeared and the working set
    after $IdleSec of idling, and closes the instance it launched. Returns the figures.
    #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$RootSuffix, [Parameter(Mandatory)][string]$Folder, [int]$CpuAtSec = 30, [int]$IdleSec = 60, [string[]]$ExtraArguments = @(), [switch]$LeaveOpen)
    $leaf = Split-Path $Folder -Leaf
    $arguments = @('/rootsuffix', $RootSuffix) + $ExtraArguments + @("`"$Folder`"")
    $proc = Start-Process -FilePath $VisualStudio.DevEnv -ArgumentList $arguments -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $windowMs = $null
        while ($clock.Elapsed.TotalSeconds -lt 240) {
            $proc.Refresh()
            if ($proc.HasExited) { throw "devenv /rootsuffix $RootSuffix exited during start-up (exit code $($proc.ExitCode))." }
            if ($proc.MainWindowHandle -ne [IntPtr]::Zero -and $proc.MainWindowTitle -like "*$leaf*") { $windowMs = $clock.Elapsed.TotalMilliseconds; break }
            Start-Sleep -Milliseconds 250
        }
        if ($null -eq $windowMs) { throw "devenv /rootsuffix $RootSuffix showed no main window naming '$leaf' within 240 s (title: '$($proc.MainWindowTitle)')." }
        $uiThread = { $p = Get-Process -Id $proc.Id; $p.Threads | Sort-Object { try { $_.StartTime } catch { [datetime]::MaxValue } } | Select-Object -First 1 }
        $uiAtWindow = (& $uiThread).TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Milliseconds ([int][math]::Max(0, ($windowMs + $CpuAtSec * 1000) - $clock.Elapsed.TotalMilliseconds))
        $ui = & $uiThread
        $uiAtCpuMark = $ui.TotalProcessorTime.TotalMilliseconds
        $processCpuAtMark = (Get-Process -Id $proc.Id).TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Milliseconds ([int][math]::Max(0, ($windowMs + $IdleSec * 1000) - $clock.Elapsed.TotalMilliseconds))
        $p = Get-Process -Id $proc.Id
        $p.Refresh()
        $hostChild = @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe' AND ParentProcessId=$($proc.Id)")
        $result = [ordered]@{
            rootSuffix          = $RootSuffix
            pid                 = $proc.Id
            mainWindowMs        = [int]$windowMs
            uiThreadId          = $ui.Id
            uiCpuAtWindowMs     = [math]::Round($uiAtWindow)
            uiCpuAtMarkMs       = [math]::Round($uiAtCpuMark)
            cpuMarkSec          = $CpuAtSec
            processCpuAtMarkMs  = [math]::Round($processCpuAtMark)
            workingSetMB        = [math]::Round($p.WorkingSet64 / 1MB, 1)
            privateMB           = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            idleSec             = $IdleSec
            pillionHostChildren = $hostChild.Count
            title               = $p.MainWindowTitle
        }
        return $result
    }
    finally {
        if (-not $LeaveOpen) { [void](Stop-LaunchedDevenv -Process $proc) }
    }
}

function Stop-LaunchedDevenv {
    <# Closes a devenv this module launched: main window close, then a kill of that pid after 60 s; waits for its hosts. #>
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)
    $Process.Refresh()
    if ($Process.HasExited) { return 'already exited' }
    $children = @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe' AND ParentProcessId=$($Process.Id)" | ForEach-Object { [int]$_.ProcessId })
    [void]$Process.CloseMainWindow()
    $how = 'main window close'
    if (-not $Process.WaitForExit(60000)) { Stop-Process -Id $Process.Id -Force; [void]$Process.WaitForExit(15000); $how = 'killed (did not close in 60 s)' }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @($children | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -gt 0) { Start-Sleep -Milliseconds 250 }
    foreach ($c in @($children | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })) { Write-Gate0Log "Pillion.Host pid $c outlived its devenv $($Process.Id); stopping it." -Level warn; Stop-Process -Id $c -Force -ErrorAction SilentlyContinue }
    return $how
}

function Measure-VsixCost {
    <#
    The VSIX cost rows: installs this build into /rootsuffix $OnSuffix (the extension on) and leaves /rootsuffix
    $OffSuffix without it (off), warms each hive up with one discarded launch (/resetsettings General, so no first-run
    dialog), then makes $Pairs pairs of launches, alternating on and off, on the same folder. Reports the medians: UI
    thread CPU in the first $CpuAtSec s after the main window, working set after $IdleSec s idle, and their differences.
    Uninstalls from $OnSuffix at the end unless -KeepInstalled.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)]$VisualStudio,
        [Parameter(Mandatory)][string]$ScratchRoot,
        [string]$OnSuffix = 'PillionG1',
        [string]$OffSuffix = 'PillionG1Off',
        [int]$Pairs = 2,
        [int]$CpuAtSec = 30,
        [int]$IdleSec = 60,
        [switch]$KeepInstalled,
        [switch]$LeaveLastOpen,
        [int]$WaitForDevenvSec = 3600
    )
    $vsix = Join-Path $RepoRoot 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
    $identity = Get-VsixManifestIdentity -VsixPath $vsix
    $folder = New-CostFolder -Root $ScratchRoot
    $waited = Wait-NoDevenv -TimeoutSec $WaitForDevenvSec -Why "the VSIX cost launches (/rootsuffix $OnSuffix and $OffSuffix)"
    $install = Install-VsixToExp -VisualStudio $VisualStudio -VsixPath $vsix -RootSuffix $OnSuffix
    $onHive = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$($VisualStudio.Major).0_$($VisualStudio.InstanceId)$OnSuffix"
    $offHive = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$($VisualStudio.Major).0_$($VisualStudio.InstanceId)$OffSuffix"
    if (-not (Find-DeployedExtension -ExpHive $onHive)) { throw "Pillion is not in $onHive after installing." }
    if (Find-DeployedExtension -ExpHive $offHive) { throw "Pillion is installed in $offHive, which must stay without it." }
    $launches = [System.Collections.Generic.List[object]]::new()
    $warm = [ordered]@{}
    $lastOpen = $null
    try {
        foreach ($suffix in $OnSuffix, $OffSuffix) {
            $w = Measure-DevenvLaunch -VisualStudio $VisualStudio -RootSuffix $suffix -Folder $folder -CpuAtSec 5 -IdleSec 10 -ExtraArguments @('/resetsettings', 'General')
            $warm[$suffix] = $w
            Write-Gate0Log "Warm-up launch /rootsuffix ${suffix}: window after $($w.mainWindowMs) ms, Pillion hosts $($w.pillionHostChildren)"
        }
        for ($i = 1; $i -le $Pairs; $i++) {
            foreach ($suffix in $OnSuffix, $OffSuffix) {
                $leave = $LeaveLastOpen -and $i -eq $Pairs -and $suffix -eq $OnSuffix
                $m = Measure-DevenvLaunch -VisualStudio $VisualStudio -RootSuffix $suffix -Folder $folder -CpuAtSec $CpuAtSec -IdleSec $IdleSec -LeaveOpen:$leave
                $m['pair'] = $i
                $m['extension'] = $(if ($suffix -eq $OnSuffix) { 'on' } else { 'off' })
                if ($leave) { $lastOpen = $m.pid }
                $launches.Add($m)
                Write-Gate0Log ("Launch {0} /rootsuffix {1}: UI thread CPU {2} ms in the first {3} s after the window; working set {4} MB after {5} s; Pillion hosts {6}" -f $i, $suffix, $m.uiCpuAtMarkMs, $CpuAtSec, $m.workingSetMB, $IdleSec, $m.pillionHostChildren)
            }
        }
    }
    finally {
        if (-not $KeepInstalled -and -not $lastOpen) {
            [void](Wait-NoDevenvOfSuffix -VisualStudio $VisualStudio -RootSuffix $OnSuffix)
            $uninstallCode = Uninstall-VsixFromExp -VisualStudio $VisualStudio -ExtensionId $identity.Id -RootSuffix $OnSuffix
        }
    }
    $median = { param($values) $s = @($values | Sort-Object); if ($s.Count -eq 0) { $null } elseif ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
    $on = @($launches | Where-Object { $_.extension -eq 'on' })
    $off = @($launches | Where-Object { $_.extension -eq 'off' })
    $uiOn = & $median ($on | ForEach-Object { $_.uiCpuAtMarkMs })
    $uiOff = & $median ($off | ForEach-Object { $_.uiCpuAtMarkMs })
    $wsOn = & $median ($on | ForEach-Object { $_.workingSetMB })
    $wsOff = & $median ($off | ForEach-Object { $_.workingSetMB })
    [ordered]@{
        method               = "devenv /rootsuffix $OnSuffix (Pillion installed) against /rootsuffix $OffSuffix (not installed), same folder, $Pairs pair(s) after one warm-up launch each; UI thread = devenv's initial thread (Process.Threads, earliest start), CPU from process start to $CpuAtSec s after the main window; working set after $IdleSec s idle"
        waitedForDevenvSec   = $waited
        install              = $install
        warmUp               = $warm
        launches             = $launches.ToArray()
        uiCpuMedianOnMs      = $uiOn
        uiCpuMedianOffMs     = $uiOff
        uiCpuDeltaMs         = $(if ($null -ne $uiOn -and $null -ne $uiOff) { [math]::Round($uiOn - $uiOff) } else { $null })
        workingSetMedianOnMB = $wsOn
        workingSetMedianOffMB = $wsOff
        workingSetDeltaMB    = $(if ($null -ne $wsOn -and $null -ne $wsOff) { [math]::Round($wsOn - $wsOff, 1) } else { $null })
        hostsStartedWhenOn   = @($on | Where-Object { $_.pillionHostChildren -ge 1 }).Count
        uninstalled          = $(if ($KeepInstalled -or $lastOpen) { "no (kept: -KeepInstanceOpen, pid $lastOpen)" } else { "exit $uninstallCode" })
        leftOpenPid          = $lastOpen
    }
}

function Wait-NoDevenvOfSuffix {
    <# Waits (up to 60 s) for every devenv /rootsuffix <suffix> of this Visual Studio to exit. #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$RootSuffix)
    $deadline = (Get-Date).AddSeconds(60)
    do {
        $running = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object { $_.CommandLine -match "/rootsuffix\s+`"?$([regex]::Escape($RootSuffix))\b" })
        if ($running.Count -eq 0) { return $true }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

#endregion

#region Versions, models, CI

function Get-OllamaModelDigests {
    <# Name, digest (first 12 characters), size and quantisation of the installed models; and what is resident (api/ps). #>
    param([string]$Endpoint = 'http://localhost:11434')
    $tags = Invoke-RestMethod -Uri "$Endpoint/api/tags" -TimeoutSec 10
    $ps = Invoke-RestMethod -Uri "$Endpoint/api/ps" -TimeoutSec 10
    [ordered]@{
        installed = @($tags.models | ForEach-Object { [ordered]@{ name = $_.name; digest = $_.digest; size = $_.size; quantization = $_.details.quantization_level; parameters = $_.details.parameter_size } })
        resident  = @($ps.models | ForEach-Object { [ordered]@{ name = $_.name; sizeVram = $_.size_vram; contextLength = $(if ($_.PSObject.Properties.Name -contains 'context_length') { $_.context_length } else { $null }); expiresAt = $_.expires_at } })
    }
}

function Clear-OllamaResidentModels {
    <# Unloads every resident Ollama model (keep_alive 0) and waits until none is resident. Returns the names unloaded. #>
    param([string]$Endpoint = 'http://localhost:11434', [int]$TimeoutSec = 60)
    $names = @((Invoke-RestMethod -Uri "$Endpoint/api/ps" -TimeoutSec 10).models | ForEach-Object { $_.name })
    foreach ($n in $names) {
        Invoke-RestMethod -Uri "$Endpoint/api/generate" -Method Post -Body (@{ model = $n; keep_alive = 0 } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 60 | Out-Null
        Write-Gate0Log "Unloaded $n from Ollama (keep_alive 0) so the live tests start from an idle GPU."
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline -and @((Invoke-RestMethod -Uri "$Endpoint/api/ps" -TimeoutSec 10).models).Count -gt 0) { Start-Sleep -Milliseconds 500 }
    return $names
}

function Get-MachineDescription {
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1)
    $ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $gpu = $null
    try { $gpu = (& nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader 2>$null | Select-Object -First 1) } catch { }
    if (-not $gpu) { $gpu = ((Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ', ') }
    "$($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) threads), $ram GB RAM, GPU $gpu, $([Environment]::OSVersion.VersionString)"
}

function Get-CiRuns {
    <# The latest CI runs on develop through gh, with the jobs of the newest; $null when gh is not available. #>
    param([string]$Repository = 'ryanjkendrick/PillionProgrammer', [string]$Branch = 'develop', [int]$Limit = 5)
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $null }
    $runs = & gh run list --repo $Repository --branch $Branch --limit $Limit --json databaseId,status,conclusion,url,headSha,createdAt,displayTitle,workflowName,event 2>$null | ConvertFrom-Json
    if (-not $runs) { return $null }
    $latest = $runs | Select-Object -First 1
    $jobs = & gh run view $latest.databaseId --repo $Repository --json jobs 2>$null | ConvertFrom-Json
    [ordered]@{
        runs = @($runs | ForEach-Object { [ordered]@{ id = $_.databaseId; title = $_.displayTitle; status = $_.status; conclusion = $_.conclusion; url = $_.url; sha = $_.headSha.Substring(0, 7); created = $_.createdAt } })
        latestJobs = @($jobs.jobs | ForEach-Object { [ordered]@{ name = $_.name; status = $_.status; conclusion = $_.conclusion; startedAt = $_.startedAt; completedAt = $_.completedAt; url = $_.url } })
    }
}

#endregion

#region The Gate 1 table

function Get-Value {
    <# Walks a dotted path through dictionaries and objects; $null when any part is missing. #>
    param($Object, [Parameter(Mandatory)][string]$Path)
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        if ($current -is [System.Collections.IDictionary]) { $current = if ($current.Contains($part)) { $current[$part] } else { $null } }
        elseif ($current.PSObject.Properties.Name -contains $part) { $current = $current.$part }
        else { return $null }
    }
    return $current
}

function Get-NearestRank {
    <# Nearest-rank percentile of numbers (nulls ignored); $null without numbers. #>
    param([object[]]$Values, [double]$Percentile)
    $sorted = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $rank = [int][math]::Ceiling($Percentile / 100 * $sorted.Count)
    return $sorted[[math]::Max(0, [math]::Min($sorted.Count - 1, $rank - 1))]
}

function Format-Ms { param($Value) if ($null -eq $Value) { 'n/a' } else { '{0:0} ms' -f [double]$Value } }

function New-Gate1Table {
    <#
    Builds the Gate 1 criteria table of this run (one Visual Studio version) from the collected data: per row the target,
    what was measured, a status (Pass, Fail, Pending product owner, Not measured) and how. Returns the rows and Markdown.
    #>
    param([System.Collections.IDictionary]$Data, [object[]]$Steps, $VisualStudio, [string]$StrongModel, [string]$FastModel)
    $v = if ($VisualStudio) { "VS$($VisualStudio.Major)" } else { 'VS?' }
    $scenarios = Get-Value $Data 'ide.scenarios'
    $tests = @(Get-Value $Data 'ide.results')
    $outcome = { param($pattern) $t = @($tests | Where-Object { $_ -and $_.name -match $pattern }); if ($t.Count -eq 0) { 'not run' } else { ($t | ForEach-Object { $_.outcome }) -join '/' } }
    $sc = { param($name) if ($scenarios -and $scenarios.Contains("$name-$v")) { $scenarios["$name-$v"] } else { $null } }
    $m = $Data['measurementsFromTests']
    $rows = [System.Collections.Generic.List[object]]::new()
    $add = { param($criterion, $target, $measured, $status, $how) $rows.Add([ordered]@{ criterion = $criterion; target = $target; measured = $measured; status = $status; how = $how }) }

    # Ghost text.
    $ghost = & $outcome 'ProposalSource_ShowsGhostText_WithTheCompetitorEnabled'
    $noList = & $outcome 'ProposalSource_NoProposal_WhileACompletionListItemIsSelected'
    $strong = & $sc 'autocomplete-strong'
    $fast = & $sc 'autocomplete-fast'
    $off = & $sc 'autocomplete-off'
    $shown = Get-Value $strong 'typing.pausesWithPillionProposal'
    & $add 'Ghost text works' 'Multi-line and single-line, Tab, Esc, word accept, next/previous; none while a completion item is selected; Tab never taken from another provider' "C#: ghost text with the competitor on $ghost, none with a selected completion item $noList; typing scenario: Pillion proposal at $shown of $(Get-Value $strong 'typing.pauses') pauses, $(Get-Value $strong 'typing.acceptedProposals') accepted" $(if ($ghost -eq 'Passed' -and $noList -eq 'Passed') { 'Pass (C#)' } else { 'Fail' }) 'IDE integration tests (canned completions, competitor on) and the typing scenario (real host and model)'

    # IntelliCode check.
    if ($VisualStudio -and $VisualStudio.Major -eq 17) {
        $ic = & $outcome 'IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff'
        & $add 'IntelliCode run-time check on 17.x' 'Status bar and Output pane report it; nothing crashes' "IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff: $ic" $(if ($ic -eq 'Passed') { 'Pass' } else { 'Fail' }) 'IDE integration test with the option off'
    }
    else { & $add 'IntelliCode run-time check on 17.x' 'Status bar and Output pane report it' 'not applicable on 18.x (run with -DevEnvPath for 17.14)' 'Not measured' '' }

    # Autocomplete latency, 1.5B on the CPU (live test only: the provider entry cannot force the CPU).
    $cpuLine = @((Get-Value $m 'autocomplete15bCpuForced'))
    $cpuP50 = Get-FirstMatchNumber -Lines $cpuLine -Pattern 'first token p50 (\d+) ms'
    $cpuP95 = Get-FirstMatchNumber -Lines $cpuLine -Pattern 'first token p50 \d+ ms, p95 (\d+) ms'
    & $add 'Autocomplete latency, 1.5B on the CPU' 'p50 600 ms, p95 1,500 ms, prompt within 1,024 tokens' "live test (num_gpu 0): first token p50 $(Format-Ms $cpuP50), p95 $(Format-Ms $cpuP95); IDE: not measured (an Ollama provider entry has no num_gpu setting)" $(if ($null -eq $cpuP50) { 'Not measured' } elseif ($cpuP50 -le 600 -and $cpuP95 -le 1500) { 'Pass' } else { 'Fail' }) 'AutocompleteLiveTests.Latency_Small_OnTheCpu_Direct'

    # Autocomplete latency, 7B on the GPU (the IDE scenario).
    $p50 = Get-Value $strong 'autocomplete.timeToSuggestion.p50'
    $p95 = Get-Value $strong 'autocomplete.timeToSuggestion.p95'
    $tokens = Get-Value $strong 'autocomplete.host.promptTokensMax'
    $liveLarge = @((Get-Value $m 'autocomplete7bGpu')) | Select-Object -First 1
    & $add "Autocomplete latency, $StrongModel on the GPU" 'p50 300 ms, p95 800 ms, prompt within 2,048 tokens' "IDE: time to suggestion p50 $(Format-Ms $p50), p95 $(Format-Ms $p95) over $(Get-Value $strong 'autocomplete.timeToSuggestion.count') suggestions; largest prompt $tokens tokens; visible after the line break p50 $(Format-Ms (Get-Value $strong 'typing.lineBreakToVisibleProposal.p50')); live: $liveLarge" $(if ($null -eq $p50) { 'Not measured' } elseif ($p50 -le 300 -and $p95 -le 800 -and ($null -eq $tokens -or $tokens -le 2048)) { 'Pass' } else { 'Fail' }) 'Typing scenario in the IDE (IAutocompleteMetrics after a reset), host counters; live test'
    & $add "Autocomplete latency, $FastModel on the GPU (information)" 'no row of its own (the 1.5B row is CPU-only)' "IDE: time to suggestion p50 $(Format-Ms (Get-Value $fast 'autocomplete.timeToSuggestion.p50')), p95 $(Format-Ms (Get-Value $fast 'autocomplete.timeToSuggestion.p95'))" 'Information' 'Typing scenario in the IDE'

    # Cloud.
    $cloud = & $sc 'cloud'
    $cloudFast = & $sc 'autocomplete-cloud'
    if ($cloudFast) {
        $cp50 = Get-Value $cloudFast 'autocomplete.timeToSuggestion.p50'; $cp95 = Get-Value $cloudFast 'autocomplete.timeToSuggestion.p95'
        & $add 'Autocomplete latency, cloud Fast' 'p50 400 ms, p95 1,000 ms' "IDE: p50 $(Format-Ms $cp50), p95 $(Format-Ms $cp95)" $(if ($cp50 -le 400 -and $cp95 -le 1000) { 'Pass' } else { 'Fail' }) 'Typing scenario routed to the Fast profile'
    }
    else { & $add 'Autocomplete latency, cloud Fast' 'p50 400 ms, p95 1,000 ms' "$($Data['cloud'])" 'Pending product owner' 'Store a key, run with -CloudProviderId' }

    # Keystroke latency.
    $kOn50 = Get-Value $fast 'typing.typeChar.p50'; $kOn95 = Get-Value $fast 'typing.typeChar.p95'
    $kS50 = Get-Value $strong 'typing.typeChar.p50'; $kS95 = Get-Value $strong 'typing.typeChar.p95'
    $kOff50 = Get-Value $off 'typing.typeChar.p50'; $kOff95 = Get-Value $off 'typing.typeChar.p95'
    if ($null -ne $kOn50 -and $null -ne $kOff50) {
        $d50 = [math]::Round($kOn50 - $kOff50, 2); $d95 = [math]::Round($kOn95 - $kOff95, 2)
        $s50 = [math]::Round($kS50 - $kOff50, 2); $s95 = [math]::Round($kS95 - $kOff95, 2)
        & $add 'Keystroke latency with autocomplete on' '+0 ms p50, +2 ms p95' ("TypeChar on the UI thread: on ({0}) p50 {1:0.00} ms, p95 {2:0.00} ms; off p50 {3:0.00} ms, p95 {4:0.00} ms; difference p50 {5:+0.00;-0.00} ms, p95 {6:+0.00;-0.00} ms (with {7}: {8:+0.00;-0.00} / {9:+0.00;-0.00} ms)" -f $FastModel, $kOn50, $kOn95, $kOff50, $kOff95, $d50, $d95, $StrongModel, $s50, $s95) $(if ($d50 -lt 0.5 -and $d95 -le 2) { 'Pass' } else { 'Fail' }) 'Typing scenario: each TypeChar through the view command chain timed until it returns (the character is in the buffer), autocomplete on against off'
    }
    else { & $add 'Keystroke latency with autocomplete on' '+0 ms p50, +2 ms p95' 'not measured' 'Not measured' '' }

    # Cache hit rate.
    $hrS = Get-Value $strong 'autocomplete.host.hitRate'; $hrF = Get-Value $fast 'autocomplete.host.hitRate'
    $steady = @((Get-Value $m 'autocomplete7bGpu') | Where-Object { $_ -match 'Steady typing' }) | Select-Object -First 1
    & $add 'Prefix cache hit rate during steady typing' '70% or more' ("IDE typing scenario (host counters over the pass): {0} with {1} ({2} requests), {3} with {4} ({5} requests); live test: {6}" -f $(if ($null -ne $hrS) { '{0:P0}' -f $hrS } else { 'n/a' }), $StrongModel, (Get-Value $strong 'autocomplete.host.requests'), $(if ($null -ne $hrF) { '{0:P0}' -f $hrF } else { 'n/a' }), $FastModel, (Get-Value $fast 'autocomplete.host.requests'), $steady) $(if ($null -eq $hrS) { 'Not measured' } elseif ($hrS -ge 0.7 -and ($null -eq $hrF -or $hrF -ge 0.7)) { 'Pass' } else { 'Fail' }) 'Gateway counters (IDiagnosticsService.GetAutocompleteCountersAsync) before and after each typing pass'

    # VSIX cost.
    $cost = $Data['cost']
    $pkg = Get-Value $Data 'ide.packageLoad'
    if ($cost) {
        # The launches resolve the working set well but not 50 ms of UI-thread CPU (launch to launch, devenv's initial thread
        # varies by a few hundred milliseconds), so the CPU budget is judged on the in-process figure: the UI thread's CPU
        # from "Begin package load" to Ready, less the idle baseline scaled to the same length. That is an upper bound for
        # Pillion's share, because Visual Studio's own work on the thread in that window is counted too.
        $loadCpu = $null
        if ($pkg -match 'to Ready: (\d+) ms over (\d+) ms;.*idle baseline after Ready: (\d+) ms over (\d+) ms') {
            $loadCpu = [math]::Round([double]$Matches[1] - [double]$Matches[3] * [double]$Matches[2] / [math]::Max(1, [double]$Matches[4]))
        }
        $cpuOk = $null -ne $loadCpu -and $loadCpu -lt 50
        & $add 'VSIX cost in devenv.exe' '0 ms blocking at start-up, under 50 ms total UI-thread CPU, added working set 60 MB or less' "in-process: $pkg => about $loadCpu ms above idle (an upper bound); on/off launches: working set delta $($cost.workingSetDeltaMB) MB (medians $($cost.workingSetMedianOnMB) / $($cost.workingSetMedianOffMB) MB), UI thread CPU delta $($cost.uiCpuDeltaMs) ms (medians $($cost.uiCpuMedianOnMs) / $($cost.uiCpuMedianOffMs) ms; within launch-to-launch noise); the package loads in the background (AsyncPackage, Begin and End package load after the folder opened)" $(if ($cpuOk -and $null -ne $cost.workingSetDeltaMB -and $cost.workingSetDeltaMB -le 60) { 'Pass' } else { 'Fail' }) 'PackageLoad_UsesLittleUiThreadCpu (UI thread sampled every 10 ms); extension on against off launches (working set after 60 s)'
    }
    else { & $add 'VSIX cost in devenv.exe' '0 ms blocking, under 50 ms UI-thread CPU, 60 MB or less' "in-process: $pkg; launches not run" 'Not measured' '' }

    # Chat first token.
    $chat = & $sc 'chat-mentions'
    $cloudChat = Get-Value $cloud 'chat'
    if ($cloudChat) { & $add 'Chat first token, cloud' 'p50 1.5 s, p95 3 s, including context assembly' "p50 $(Format-Ms (Get-Value $cloudChat 'firstText.p50')), p95 $(Format-Ms (Get-Value $cloudChat 'firstText.p95'))" $(if ((Get-Value $cloudChat 'firstText.p50') -le 1500 -and (Get-Value $cloudChat 'firstText.p95') -le 3000) { 'Pass' } else { 'Fail' }) 'Chat window, five messages' }
    else { & $add 'Chat first token, cloud' 'p50 1.5 s, p95 3 s, including context assembly' "$($Data['cloud']); local $StrongModel (information): first text p50 $(Format-Ms (Get-Value $chat 'chat.firstText.p50')), p95 $(Format-Ms (Get-Value $chat 'chat.firstText.p95')) over 5 messages" 'Pending product owner' 'Chat window: input, Enter, first visible reply text (50 ms flushes)' }

    # Context assembly.
    $ctx = @((Get-Value $m 'contextAssembly'))
    # The slowest of the benchmark's cases (exact tiktoken, calibrated estimate, cached summary) is judged.
    $ctxP50 = (@($ctx | ForEach-Object { if ($_ -match 'assembly p50 ([\d.]+) ms') { [double]$Matches[1] } }) | Measure-Object -Maximum).Maximum
    $ctxP95 = (@($ctx | ForEach-Object { if ($_ -match 'assembly p50 [\d.]+ ms, p95 ([\d.]+) ms') { [double]$Matches[1] } }) | Measure-Object -Maximum).Maximum
    & $add 'Context assembly without retrieval' 'p50 150 ms, p95 400 ms' "benchmark, slowest case: p50 $(Format-Ms $ctxP50), p95 $(Format-Ms $ctxP95) ($($ctx -join ' / '))" $(if ($null -eq $ctxP50) { 'Not measured' } elseif ($ctxP50 -le 150 -and $ctxP95 -le 400) { 'Pass' } else { 'Fail' }) 'ContextAssemblyBenchmarkTests (40 runs, real tokenizers)'

    # Inline chat preview first line.
    $inline = & $sc 'inline-chat'
    $runs = @(Get-Value $inline 'runs')
    $fl = @($runs | ForEach-Object { Get-Value $_ 'firstLineMs' })
    $ft = @($runs | ForEach-Object { Get-Value $_ 'firstTextMs' })
    $flP50 = Get-NearestRank $fl 50; $flP95 = Get-NearestRank $fl 95
    $enter = & $outcome 'InlineDifferenceView_EnterAppliesOneEditWithOneUndoStep'
    $esc = & $outcome 'InlineDifferenceView_EscDiscards_LeavingNothingToUndo'
    $untouched = @($runs | Where-Object { (Get-Value $_ 'bufferUntouchedAfterEsc') -eq $true }).Count
    & $add 'Inline chat preview, first line' 'p50 2 s, p95 5 s; Enter one edit and one undo step; Esc leaves nothing to undo' "first line p50 $(Format-Ms $flP50), p95 $(Format-Ms $flP95); first text p50 $(Format-Ms (Get-NearestRank $ft 50)) ($($runs.Count) runs: extract and /doc, 5 each, $StrongModel); Enter test $enter, Esc test $esc; buffer untouched after Esc in $untouched of $($runs.Count) runs" $(if ($null -eq $flP50) { 'Not measured' } elseif ($flP50 -le 2000 -and $flP95 -le 5000 -and $enter -eq 'Passed' -and $esc -eq 'Passed') { 'Pass' } else { 'Fail' }) 'Inline chat on the real host: command, prompt, Enter, ProposalChanged times; IDE integration tests'

    # Mentions and inspection.
    $pins = @(Get-Value $chat 'mentions.pins')
    $items = @(Get-Value $chat 'mentions.inspectorItems')
    $hasAll = (Get-Value $chat 'outbound.hasChat') -and (Get-Value $chat 'outbound.hasAutocomplete') -and (Get-Value $chat 'outbound.hasInlineEdit')
    $mentionTest = & $outcome 'Mentions_TypeAtPickAFileAndSend'
    & $add 'Mentions and inspection' '@file, @selection and @errors resolve; the inspector shows the context before sending; every task class has Outbound records' "pins: $($pins -join ', '); inspector: $($items.Count) items ($($items -join '; ')); notices: $(Get-Value $chat 'mentions.mentionNotice'); Outbound task classes: $(@((Get-Value $chat 'outbound.byTaskClass') | ForEach-Object { $_.Keys }) -join ', '); mention integration test $mentionTest" $(if ($pins.Count -ge 3 -and $items.Count -ge 3 -and $hasAll) { 'Pass' } else { 'Fail' }) 'Chat window on the real host: picker, Context Inspector, send; IDiagnosticsService.GetOutboundRecordsAsync'

    # Degradation, evaluation, offline, CI, flags.
    $deg = $Steps | Where-Object { $_.name -like 'j. *' } | Select-Object -First 1
    & $add 'Degradation' 'p95 over 1.5 s for five minutes raises the debounce to 350 ms, single-line; recovers' "$(Get-Value $deg 'details.Summary')" $(if ($deg -and $deg.status -eq 'Pass') { 'Pass' } elseif ($deg) { $deg.status } else { 'Not measured' }) 'Component test over the real pipe with a slow fake host (AutocompleteRealHostTests.SlowHost_Degrades...), unit tests of the rule'
    & $add 'Completion evaluation baseline' 'Golden set against the recommended local model; exact match, edit similarity, latency' 'no golden-set repository yet (work item 1.17)' 'Pending product owner' ''
    & $add 'Offline run' 'Every check above with the network adapter disabled' 'tools/gate0/Invoke-OfflineRun.ps1 -Phase 1 (dry-run with -WhatIf only)' 'Pending product owner' 'Elevated, by the product owner'
    $ci = $Steps | Where-Object { $_.name -like 'm. *' } | Select-Object -First 1
    & $add 'CI' 'Green on develop with the 18.x job and a 17.14 integration-test job' "$(Get-Value $ci 'details.Summary')" $(if ($ci) { $ci.status } else { 'Not measured' }) 'gh run list / gh run view'
    $b1 = $Steps | Where-Object { $_.name -like 'b1. *' } | Select-Object -First 1
    & $add 'Feature flags' 'Every work item behind a flag; tests green' "unit and component tests: $(Get-Value $b1 'details.Summary'); flags autocomplete, inlineChat, mentions, outboundLogView and one per adapter (README, Feature flags)" $(if ($b1 -and $b1.status -eq 'Pass') { 'Pass' } elseif ($b1) { $b1.status } else { 'Not measured' }) 'Test run; README flag list'

    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("## Gate 1 criteria ($v)")
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Criterion | Target | Measured | Status | How |')
    [void]$md.AppendLine('| --- | --- | --- | --- | --- |')
    foreach ($r in $rows) { [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} |' -f (($r.criterion, $r.target, $r.measured, $r.status, $r.how) | ForEach-Object { ([string]$_ -replace '\|', '\|' -replace "`r?`n", ' ') }))) }
    [pscustomobject]@{ Rows = $rows.ToArray(); Markdown = $md.ToString() }
}

#endregion

Export-ModuleMember -Function *-*
