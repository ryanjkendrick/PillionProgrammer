#Requires -Version 7.5
<#
.SYNOPSIS
    The offline run of design section 15: steps d to g of Invoke-Gate0.ps1 with every non-loopback network adapter
    disabled, on local models only. For the product owner to run.

.DESCRIPTION
    This script switches the machine's network off for several minutes. It refuses to run unless it is started
    with -IAmReady from an elevated PowerShell 7 prompt. It then:

      1. lists the network adapters it will disable (every adapter whose status is Up, or the ones named with
         -AdapterName; loopback is never listed by Get-NetAdapter);
      2. backs up settings.json and writes the run's settings (an Ollama provider, and a key-less placeholder cloud
         provider when none is configured, because Pillion only reports Offline when a cloud provider is set up);
      3. launches the Experimental Instance and waits for Ready;
      4. disables the adapters with Disable-NetAdapter and waits for the status to read Offline (Pillion probes on
         every network change and every 60 s; the wait is capped at -ProbeWaitSec, default 70 s), or Local-only when
         -ForceLocalOnly is given;
      5. runs the local chat, host-kill and Local-only checks (Gate0.Checks.psm1) and confirms the status reads
         Offline (or Local-only) again after the restarts;
      6. re-enables exactly the adapters it disabled in a finally block, whatever happened, then closes the instance,
         checks that no Pillion.Host survives and restores settings.json byte for byte.

    Results go to tools/gate0/out/<timestamp>-offline/ (gate0-results.json and gate0-summary.md).

    With -Phase 1 (Gate 1, work item 1.18) the run also covers phase 1 offline: the settings route autocomplete to
    -FastModel (Local-fast) and chat and inline edits to -StrongModel (Local-strong); after step g the instance is closed
    and, with the adapters still disabled, the Visual Studio integration tests run in the Experimental Instance with the
    Gate 1 scenarios switched on (tools/gate1/Gate1.Scenarios.psm1, Invoke-GateIdeRun): typing with ghost text (strong
    model, fast model, autocomplete off), inline chat (extract a method, /doc), chat first token, @file, @errors and
    @selection with the Context Inspector, and the Outbound records of every task class. Results then go to
    tools/gate1/out/<timestamp>-offline/ (gate1-results.json and gate1-summary.md), with the scenario JSON files under
    ide-VS<major>/scenarios. The scenario run waits until no devenv.exe runs at all before it starts.

    With -Phase 2 (Gate 2, work item 2.17) the run covers phase 2 offline as well: before the network goes off it creates the
    Gate 2 generated solution and builds the 1,000,000-line index in the data folder with the bundled model (as step d of
    tools/gate2/Invoke-Gate2.ps1 does), turns every phase 2 flag on, and after step g runs the integration tests with the Gate 1
    and Gate 2 scenarios switched on, with the adapters still disabled: indexing with the bundled model, keystrokes while
    indexing, save to searchable, the branch switches, search and context assembly on the 100,000-chunk index, a chat that
    cites (Ollama), the Index window, workspace queries and persistence; then the encryption-at-rest checks. Embeddings come
    from the bundled model only and chat from Ollama only. Results go to tools/gate2/out/<timestamp>-offline/; the gate's
    indexes are deleted at the end.

    Dry run: `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -WhatIf` (add -Phase 1 for Gate 1, -Phase 2 for Gate 2) lists the adapters and
    shows what Disable-NetAdapter and Enable-NetAdapter would do, without elevation and without changing anything.

    If the script is interrupted so hard that the finally block cannot run (for example the window is closed),
    re-enable the adapters by hand: Get-NetAdapter | Where-Object Status -eq Disabled | Enable-NetAdapter.

.PARAMETER IAmReady
    Required. Confirms you expect the network to go off for several minutes.
.PARAMETER AdapterName
    Only these adapters (names as Get-NetAdapter shows them). Default: every adapter whose status is Up.
.PARAMETER ProbeWaitSec
    The longest wait for Offline after the adapters go down. Default 70 (one 60 s probe cadence plus margin).
.PARAMETER ForceLocalOnly
    Turn Local-only on before launch; the status is then expected to read Local-only instead of Offline.
.PARAMETER Phase
    0 (default): the Gate 0 checks. 1: also the phase 1 scenarios (autocomplete, inline chat, chat, mentions) offline.
    2: also the phase 2 scenarios (Gate 2: indexing, retrieval, persistence, encryption) offline, after building the big index.
.PARAMETER StrongModel
    With -Phase 1: the Local-strong model (chat, inline edits). Default qwen2.5-coder:7b.
.PARAMETER FastModel
    With -Phase 1: the Local-fast model (autocomplete). Default qwen2.5-coder:1.5b.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$IAmReady,
    [string[]]$AdapterName,
    [int]$ProbeWaitSec = 70,
    [switch]$ForceLocalOnly,
    [string]$ProviderId = 'ollama',
    [string]$Model = 'gemma4:e2b',
    [string]$OllamaEndpoint = 'http://localhost:11434',
    [string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) 'pillion-gate0'),
    [int]$LocalOnlyHoldSec = 65,
    [ValidateSet(0, 1, 2)][int]$Phase = 0,
    [string]$StrongModel = 'qwen2.5-coder:7b',
    [string]$FastModel = 'qwen2.5-coder:1.5b'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-AdaptersToDisable {
    $all = @(Get-NetAdapter)
    if ($AdapterName) {
        $missing = @($AdapterName | Where-Object { $_ -notin $all.Name })
        if ($missing.Count -gt 0) { throw "No adapter named: $($missing -join ', '). Adapters: $($all.Name -join ', ')" }
        return @($all | Where-Object { $_.Name -in $AdapterName })
    }
    return @($all | Where-Object { $_.Status -eq 'Up' })
}

$adapters = Get-AdaptersToDisable
Write-Host 'Network adapters this run disables (and re-enables at the end):'
if ($adapters.Count -eq 0) { Write-Host '  (none are Up)' }
foreach ($a in $adapters) { Write-Host ("  {0,-30} {1,-12} {2}  {3}" -f $a.Name, $a.Status, $a.MacAddress, $a.InterfaceDescription) }

if (-not $IAmReady) {
    Write-Warning 'Refusing to run: this script turns the network off. Start it again with -IAmReady from an elevated PowerShell 7 prompt (or add -WhatIf for a dry run).'
    exit 1
}

if ($WhatIfPreference) {
    Write-Host ''
    Write-Host 'Dry run (-WhatIf): nothing is changed, Visual Studio is not started.'
    foreach ($a in $adapters) {
        Disable-NetAdapter -Name $a.Name -Confirm:$false -WhatIf
        Enable-NetAdapter -Name $a.Name -Confirm:$false -WhatIf
    }
    Write-Host "Then: launch the Experimental Instance, wait up to $ProbeWaitSec s for '$(if ($ForceLocalOnly) { 'Local-only' } else { 'Offline' })', run the local chat, host-kill and Local-only checks, re-enable the adapters, close the instance and restore settings.json."
    if ($Phase -ge 1) {
        Write-Host "Phase 1: settings route autocomplete to $ProviderId/$FastModel (Local-fast) and chat and inline edits to $ProviderId/$StrongModel (Local-strong)."
        Write-Host '         After the Local-only check, close the instance and, with the adapters still disabled, run tools/vs-tests/Invoke-IdeTests.ps1'
        Write-Host '         with PILLION_GATE_SCENARIOS=1: the integration tests plus typing with ghost text (strong, fast, off), inline chat'
        Write-Host '         (extract a method, /doc), chat first token, @file/@errors/@selection with the Context Inspector and the Outbound'
        Write-Host '         records of every task class. Results in tools/gate1/out/<timestamp>-offline/.'
    }
    if ($Phase -eq 2) {
        Write-Host 'Phase 2: before the adapters go down, create the Gate 2 generated solution and index 1,000,000 generated lines into the data'
        Write-Host '         folder with the bundled model on the CPU (about 20 minutes). With the adapters down, the IDE run also has'
        Write-Host '         PILLION_GATE2_SCENARIOS=1 (indexing, keystrokes while indexing, save to searchable, branch switches, search and'
        Write-Host '         context assembly on the big index, a cited chat on Ollama, the Index window, workspace queries, persistence),'
        Write-Host '         then the encryption-at-rest checks. The gate indexes are deleted at the end. Results in tools/gate2/out/<timestamp>-offline/.'
    }
    exit 0
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning 'Refusing to run: Disable-NetAdapter needs an elevated prompt. Start PowerShell 7 as administrator and run this again with -IAmReady.'
    exit 1
}
if ($adapters.Count -eq 0) { Write-Warning 'No adapter is Up; nothing to disable.'; exit 1 }

Import-Module (Join-Path $PSScriptRoot 'Gate0.Checks.psm1') -Force
if ($Phase -ge 1) { Import-Module (Join-Path $repo 'tools\gate1\Gate1.Scenarios.psm1') -Force }
if ($Phase -eq 2) { Import-Module (Join-Path $repo 'tools\gate2\Gate2.Scenarios.psm1') -Force }
$runDir = switch ($Phase) { 2 { Initialize-Gate0Run -RepoRoot $repo -Label 'offline' -OutRoot 'tools/gate2/out' } 1 { Initialize-Gate0Run -RepoRoot $repo -Label 'offline' -OutRoot 'tools/gate1/out' } default { Initialize-Gate0Run -RepoRoot $repo -Label 'offline' } }
$resultPrefix = switch ($Phase) { 2 { 'gate2' } 1 { 'gate1' } default { 'gate0' } }
$gate2Scratch = Join-Path ([IO.Path]::GetTempPath()) 'pillion-gate2-offline'
$gate2Solution = $null
$gate2BigRepo = Join-Path $gate2Scratch 'repo-synthetic-1000000'
$runStart = [DateTimeOffset]::Now
$expect = if ($ForceLocalOnly) { 'Local-only' } else { 'Offline' }
$run = [ordered]@{
    kind       = 'Invoke-OfflineRun'
    startedAt  = $runStart.ToString('o')
    machine    = $env:COMPUTERNAME
    parameters = [ordered]@{ AdapterName = $AdapterName; ProbeWaitSec = $ProbeWaitSec; ForceLocalOnly = [bool]$ForceLocalOnly; ProviderId = $ProviderId; Model = $Model; Phase = $Phase; StrongModel = $StrongModel; FastModel = $FastModel }
    adapters   = @($adapters | ForEach-Object { [ordered]@{ name = $_.Name; description = $_.InterfaceDescription; statusBefore = [string]$_.Status } })
    versions   = [ordered]@{}
}
$steps = [System.Collections.Generic.List[object]]::new()
$disabled = [System.Collections.Generic.List[string]]::new()

$prereq = $null
$pre = Invoke-Gate0Step -Name 'Prerequisites' -Body {
    $script:prereq = Assert-Gate0Prerequisites -Model $Model -OllamaEndpoint $OllamaEndpoint
    if (-not (Find-DeployedExtension -ExpHive $script:prereq.VisualStudio.ExpHive)) { throw 'Pillion is not deployed to the Experimental Instance; run tools/gate0/Invoke-Gate0.ps1 or tools/gate1/Invoke-Gate1.ps1 (which build and deploy) first.' }
    if ($Phase -ge 1) {
        foreach ($m in $StrongModel, $FastModel) { if (-not (Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $m).ModelPulled) { throw "The model '$m' is not pulled in Ollama." } }
    }
    $models = if ($Phase -ge 1) { "$Model, $StrongModel and $FastModel" } else { $Model }
    [ordered]@{ Summary = "$($script:prereq.VisualStudio.DisplayName) $($script:prereq.VisualStudio.Version); Ollama $($script:prereq.Ollama.Version) with $models" }
}
$steps.Add($pre)
if ($pre.status -ne 'Pass') { Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title 'Offline run' -FilePrefix $resultPrefix; exit 2 }
$vs = $prereq.VisualStudio
$run.versions['Visual Studio'] = "$($vs.DisplayName) $($vs.Version)"
$run.versions['Ollama'] = $prereq.Ollama.Version
$deployed = Find-DeployedExtension -ExpHive $vs.ExpHive
$run.versions['Pillion.Host --version'] = Get-HostVersionText -HostExe $deployed.HostExe

try {
    $providerInfo = $null
    $steps.Add((Invoke-Gate0Step -Name 'c. Back up and prepare settings.json' -Body {
                $b = Backup-PillionSettings
                $script:providerInfo = Set-Gate0Settings -ProviderId $ProviderId -Model $Model -OllamaEndpoint $OllamaEndpoint -LocalOnly ([bool]$ForceLocalOnly)
                $routing = ''
                if ($Phase -ge 1) {
                    # Phase 1 routing: autocomplete on Local-fast, chat and inline edits on Local-strong, every phase 1 flag on.
                    $settings = Read-PillionSettings
                    $local = @($settings['providers']) | Where-Object { $_ -and $_['id'] -eq $ProviderId } | Select-Object -First 1
                    $local['models'] = @(@($local['models']) + $StrongModel + $FastModel | Where-Object { $_ } | Select-Object -Unique)
                    foreach ($section in 'routing', 'features') { if (-not $settings.Contains($section) -or $null -eq $settings[$section]) { $settings[$section] = [ordered]@{} } }
                    foreach ($section in 'tasks', 'profiles') { if (-not $settings['routing'].Contains($section) -or $null -eq $settings['routing'][$section]) { $settings['routing'][$section] = [ordered]@{} } }
                    $settings['routing']['tasks']['autocomplete'] = 'localFast'
                    $settings['routing']['tasks']['inlineEdit'] = 'localStrong'
                    $settings['routing']['tasks']['chat'] = 'localStrong'
                    $settings['routing']['profiles']['localFast'] = "$ProviderId/$FastModel"
                    $settings['routing']['profiles']['localStrong'] = "$ProviderId/$StrongModel"
                    foreach ($flag in 'autocomplete', 'inlineChat', 'mentions', 'outboundLogView') { $settings['features'][$flag] = $true }
                    if ($Phase -eq 2) { foreach ($flag in 'localStore', 'indexing', 'embeddings', 'bundledEmbeddings', 'workspaceQueries', 'indexWindow', 'retrieval') { $settings['features'][$flag] = $true } }
                    Write-PillionSettings -Settings $settings
                    $routing = "; phase 1 routing: Local-fast $FastModel, Local-strong $StrongModel"
                }
                [ordered]@{ Summary = "backup sha256 $($b.Sha256); local-only forced: $([bool]$ForceLocalOnly); cloud placeholder added: $($script:providerInfo.cloudPlaceholderAdded)$routing"; providers = $script:providerInfo }
            }))
    if ($steps[-1].status -ne 'Pass') { throw 'Settings could not be prepared.' }

    if ($Phase -eq 2) {
        # Before the network goes off: the generated solution and the big index in the data folder (the big-index scenarios read it).
        $steps.Add((Invoke-Gate0Step -Name 'Phase 2: generated solution and the 1,000,000-line index (network still on)' -Body {
                    New-Item -ItemType Directory -Force -Path $gate2Scratch | Out-Null
                    $script:gate2Solution = New-Gate2Solution -Root $gate2Scratch
                    $removed = Remove-Gate2Indexes -UnderPath $gate2Scratch
                    $json = Join-Path $runDir 'index-synthetic-1000000-cpu.json'
                    $envVars = @{ PILLION_GATE2_INDEX = '1'; PILLION_GATE2_LINES = '1000000'; PILLION_GATE2_SOURCE = 'synthetic'; PILLION_GATE2_EP = 'cpu'; PILLION_GATE2_REPO = $gate2BigRepo; PILLION_GATE2_OUT = $json; PILLION_GATE2_DATA = (Join-Path $env:LOCALAPPDATA 'PillionProgrammer') }
                    foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
                    try { $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets @('tests\Pillion.Host.Tests\Pillion.Host.Tests.csproj') -Filter 'FullyQualifiedName~Gate2IndexingTests' -ResultsDir (Join-Path $runDir 'tests-index') -LogPath (Join-Path $runDir 'test-index.log') -NoBuild }
                    finally { foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $null) } }
                    [ordered]@{ Status = ($r.Totals.passed -eq 1) ? 'Pass' : 'Fail'; Summary = "solution $($gate2Solution.Solution) ($($gate2Solution.Lines) lines); big index: $(if (Test-Path $json) { (Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).chunks } else { 'no result' }) chunks; earlier gate indexes removed: $($removed.Count)" }
                }))
    }

    $folder = Join-Path $ScratchRoot ("folder-" + (Split-Path $runDir -Leaf))
    $launch = Invoke-Gate0Step -Name 'd. Launch Experimental Instance (network still on)' -Body { Invoke-ExpLaunchCheck -VisualStudio $vs -Folder $folder }
    $steps.Add($launch)
    if ($launch.status -ne 'Pass') { throw 'The Experimental Instance did not reach Ready.' }

    $steps.Add((Invoke-Gate0Step -Name "Disable adapters and wait for $expect" -Body {
                $offAt = [DateTimeOffset]::Now
                foreach ($a in $adapters) {
                    Disable-NetAdapter -Name $a.Name -Confirm:$false
                    $disabled.Add($a.Name)
                    Write-Gate0Log "Disabled adapter '$($a.Name)'"
                }
                $s = Wait-ChatStatus -Until { param($t) (Get-HostStateFromStatus $t).Connectivity -eq $expect } -TimeoutSec $ProbeWaitSec -What $expect
                $conn = @(Get-HostLogEntries -Since $offAt | Where-Object { $_.message -like 'Connectivity *' } | ForEach-Object { "$($_.ts) $($_.message)" })
                [ordered]@{ Summary = "status '$($s.Status)' $($s.WaitMs) ms after the adapters went down"; disabled = $disabled.ToArray(); waitMs = $s.WaitMs; statusSequence = $s.Seen; statusBar = Get-ExpStatusBarText; connectivityLog = $conn }
            }))

    $steps.Add((Invoke-Gate0Step -Name 'e. Local chat check (offline)' -Body { Invoke-LocalChatCheck -Model $Model -ProviderName $providerInfo.localProviderName }))
    $steps.Add((Invoke-Gate0Step -Name 'f. Host-kill check (offline)' -Body { Invoke-HostKillCheck -Model $Model -ProviderName $providerInfo.localProviderName }))
    $steps.Add((Invoke-Gate0Step -Name "Connectivity reads $expect after the restarts" -Body {
                $s = Wait-ChatStatus -Until { param($t) (Get-HostStateFromStatus $t).Connectivity -eq $expect } -TimeoutSec $ProbeWaitSec -What $expect
                [ordered]@{ Summary = "status '$($s.Status)' after $($s.WaitMs) ms"; statusBar = Get-ExpStatusBarText }
            }))
    $steps.Add((Invoke-Gate0Step -Name 'g. Local-only check (offline)' -Body { Invoke-LocalOnlyCheck -Model $Model -ProviderName $providerInfo.localProviderName -AlreadyOn:$ForceLocalOnly -HoldSec $LocalOnlyHoldSec }))

    if ($Phase -ge 1) {
        # The phase 1 scenarios run in the IDE test framework's own instance: close this one first (the network stays off).
        $steps.Add((Invoke-Gate0Step -Name 'Phase 1: close the instance (network still off)' -Body {
                    $d = [ordered]@{ logExcerpts = Save-LogExcerpts -Since $runStart; close = Stop-ExpInstance }
                    $sv = Test-NoHostSurvivors
                    $d['survivingHosts'] = $sv.survivors
                    $d['Status'] = (@($sv.survivors).Count -eq 0) ? 'Pass' : 'Fail'
                    $d['Summary'] = "closed by $($d['close']); surviving hosts: $(@($sv.survivors).Count)"
                    $d
                }))
        $steps.Add((Invoke-Gate0Step -Name 'Phase 1: integration tests and Gate 1 scenarios with the network off (autocomplete, inline chat, chat, mentions)' -Body {
                    $envVars = @{ PILLION_METRICS_WINDOW_SECONDS = '30'; PILLION_GATE_PROVIDER = $ProviderId; PILLION_GATE_STRONG_MODEL = $StrongModel; PILLION_GATE_FAST_MODEL = $FastModel }
                    if ($Phase -eq 2 -and $gate2Solution) { $envVars['PILLION_GATE2_SCENARIOS'] = '1'; $envVars['PILLION_GATE2_SOLUTION'] = $gate2Solution.Solution; $envVars['PILLION_GATE2_BIG_REPO'] = $gate2BigRepo }
                    $r = Invoke-GateIdeRun -RepoRoot $repo -VisualStudio $vs -RunDir $runDir -ScenarioEnvironment $envVars -WaitForDevenvSec 600
                    $failedTests = @($r.Results | Where-Object Outcome -EQ 'Failed')
                    $run['phase1Scenarios'] = $r.Scenarios
                    $chat = $r.Scenarios["chat-mentions-VS$($vs.Major)"]
                    $classes = if ($chat) { @($chat['outbound']['byTaskClass'].Keys) -join ', ' } else { 'n/a' }
                    [ordered]@{
                        Status      = ($r.ExitCode -eq 0 -and $failedTests.Count -eq 0 -and $r.Results.Count -gt 0) ? 'Pass' : 'Fail'
                        Summary     = "$(@($r.Results | Where-Object Outcome -EQ 'Passed').Count) passed, $($failedTests.Count) failed; scenarios: $($r.Scenarios.Keys -join ', '); Outbound task classes offline: $classes"
                        trx         = $r.Trx
                        failed      = @($failedTests | ForEach-Object { "$($_.Name): $($_.Error)" })
                        hostKill    = $r.HostKill
                        packageLoad = $r.PackageLoad
                    }
                }))
    }
}
catch {
    Write-Gate0Log "Offline run stopped: $($_.Exception.Message)" -Level error
}
finally {
    # The network comes back first, whatever happened above.
    $reenabled = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $disabled) {
        try { Enable-NetAdapter -Name $name -Confirm:$false; $reenabled.Add([ordered]@{ name = $name; ok = $true }); Write-Gate0Log "Re-enabled adapter '$name'" }
        catch { $reenabled.Add([ordered]@{ name = $name; ok = $false; error = $_.Exception.Message }); Write-Gate0Log "Could not re-enable '$name': $($_.Exception.Message). Run: Enable-NetAdapter -Name '$name'" -Level error }
    }
    $run['adaptersReenabled'] = $reenabled.ToArray()

    $steps.Add((Invoke-Gate0Step -Name 'i. Re-enable network, close, check for surviving hosts, restore settings' -Body {
                $d = [ordered]@{ reenabled = $reenabled.ToArray() }
                if (Get-ExpPid) {
                    $d['logExcerpts'] = Save-LogExcerpts -Since $runStart
                    $d['close'] = Stop-ExpInstance
                    $sv = Test-NoHostSurvivors
                    $d['hostPidsSeen'] = $sv.hostPidsSeen
                    $d['survivingHosts'] = $sv.survivors
                }
                $r = Restore-PillionSettings
                $d['settingsRestored'] = $r
                $bad = @($reenabled | Where-Object { -not $_.ok }).Count -gt 0 -or ($d['survivingHosts'] -and @($d['survivingHosts']).Count -gt 0) -or ($r.Restored -and -not $r.MatchesBackup)
                $d['Status'] = $bad ? 'Fail' : 'Pass'
                $d['Summary'] = "adapters re-enabled: $(($reenabled | ForEach-Object { "$($_.name)=$($_.ok)" }) -join ', '); closed by $($d['close']); surviving hosts: $(if ($d['survivingHosts']) { $d['survivingHosts'] -join ', ' } else { 'none' }); settings restored: $($r.MatchesBackup)"
                $d
            }))
    if ($Phase -eq 2) {
        $steps.Add((Invoke-Gate0Step -Name 'Phase 2: encryption at rest after the offline run; delete the gate indexes' -Body {
                    $dataRoot = Join-Path $env:LOCALAPPDATA 'PillionProgrammer'
                    $checks = @(Get-PillionIndexFolders | Where-Object { $_.Root -and $_.Root.StartsWith($gate2Scratch, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 2 | ForEach-Object { Test-PlainSqliteOpen -Path (Join-Path $_.Folder 'index.db') -Scratch $gate2Scratch })
                    if (Test-Path (Join-Path $dataRoot 'conversations.db')) { $checks += Test-PlainSqliteOpen -Path (Join-Path $dataRoot 'conversations.db') -Scratch $gate2Scratch }
                    $plain = Find-PlainText -Folders @($dataRoot) -Marker 'Handles step 5 of type 417.'
                    $removed = @(Remove-Gate2Indexes -UnderPath $gate2Scratch)
                    $bad = @($checks | Where-Object { $_.Opened -eq $true }).Count -gt 0 -or $plain.Hits.Count -gt 0
                    [ordered]@{ Status = $bad ? 'Fail' : 'Pass'; Summary = "plain SQLite: $(($checks | ForEach-Object { $_.Outcome }) -join '; '); marker found in $($plain.Hits.Count) files; gate indexes deleted: $($removed.Count)" }
                }))
    }
    $title = switch ($Phase) { 2 { 'Offline run (network adapters disabled), phase 2' } 1 { 'Offline run (network adapters disabled), phase 1' } default { 'Offline run (network adapters disabled)' } }
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title $title -FilePrefix $resultPrefix
}

$failed = @($steps | Where-Object status -EQ 'Fail')
Write-Gate0Log ("Offline run done: {0} steps, {1} failed. Results in {2}" -f $steps.Count, $failed.Count, $runDir) -Level ($failed.Count ? 'fail' : 'pass')
exit ($failed.Count ? 1 : 0)
