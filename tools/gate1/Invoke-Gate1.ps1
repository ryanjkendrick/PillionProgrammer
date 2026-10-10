#Requires -Version 7.5
<#
.SYNOPSIS
    Collects the Gate 1 evidence (work item 1.18): build, unit and live tests, the IDE performance scenarios with the real
    Pillion Host and local models, the VSIX cost launches, the host-kill test, CI status, and the Gate 1 criteria table.

.DESCRIPTION
    Run from a plain PowerShell 7 prompt in the repository root, with Ollama running and the models pulled:

        pwsh tools/gate1/Invoke-Gate1.ps1
        pwsh tools/gate1/Invoke-Gate1.ps1 -SkipBuild -SkipTests
        pwsh tools/gate1/Invoke-Gate1.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
        pwsh tools/gate1/Invoke-Gate1.ps1 -CloudProviderId openai          # after storing that provider's key

    Steps, each timed and logged to tools/gate1/out/<timestamp>-gate1/ (git-ignored):
      a. Build the full solution (Release, Visual Studio 2026's MSBuild, no deployment) and record the versions: VSIX,
         Pillion Host, Ollama, Visual Studio, the machine, and the models by digest.
      b. Unit and component tests (dotnet test on the NoVsix filter, Category!=Live) with TRX files, then the live tests
         (Category=Live) of Pillion.ModelGateway.Tests and Pillion.Host.Tests against Ollama; the latency, cache, context
         assembly and rendering figures the tests print are collected.
      c. Back up %LOCALAPPDATA%\PillionProgrammer\settings.json (restored byte for byte at the end) and write the run's
         settings: the Ollama provider with -StrongModel as Local-strong (chat, inline edits) and -FastModel as Local-fast
         (autocomplete); every phase 1 feature flag on.
      d-f, h, i, k. The Visual Studio integration tests in the Experimental Instance of the driven version (the whole suite,
         including the host-kill test, with the Gate 1 scenarios of PillionPackageTests.Gate1.cs switched on): typing a
         fixed 40-line C# file at about 80 ms a character with a pause at each line break, with autocomplete routed to the
         strong model, then the fast one, then switched off (time to suggestion, show and acceptance counts, degradation,
         host cache hit rate and prompt token counts, TypeChar time on the UI thread); five inline chat runs each of
         "extract the validation into a method" and /doc (first preview text, first line); five local chat messages (first
         token); @file, @errors and @selection pinned, the Context Inspector opened and the message sent; the Outbound records
         of every task class. PILLION_METRICS_WINDOW_SECONDS is short, so metrics-YYYYMMDD.jsonl gets lines during the run.
         Before installing into the Experimental Instance or starting the run, waits until no devenv.exe runs at all.
      g. VSIX cost: this build installed into /rootsuffix PillionG1 against /rootsuffix PillionG1Off without it, launched in
         pairs on the same folder: UI thread CPU in the first 30 s after the main window, working set after 60 s idle.
      j. Degradation: the component test over the real pipe with a slow fake host (step b's result).
      l. Completion evaluation baseline: pending the product owner (no golden-set repository yet).
      m. CI: the latest runs on develop (gh).
      n. Restore settings.json, uninstall from PillionG1, check that no devenv.exe or Pillion.Host.exe this run started is
         left, and leave -StrongModel resident in Ollama.

    The script never reads, stores or prints an API key: the cloud rows run only with -CloudProviderId when
    `cmdkey /list:PillionProgrammer/<id>` shows a stored key; otherwise they are recorded as pending.

.PARAMETER SkipBuild
    Skip the build (the existing Release build is used and its versions recorded).
.PARAMETER SkipTests
    Skip step b (unit, component and live tests).
.PARAMETER SkipLiveTests
    Run the unit and component tests but not the live ones.
.PARAMETER SkipIde
    Skip the IDE run (scenarios and integration tests).
.PARAMETER SkipCost
    Skip the VSIX cost launches.
.PARAMETER KeepInstanceOpen
    Leave the last extension-on cost launch (/rootsuffix PillionG1) open, with Pillion still installed there; settings.json
    is still restored.
.PARAMETER DevEnvPath
    The devenv.exe whose instances are driven. Default: the newest Visual Studio. The build always uses the newest
    Visual Studio's MSBuild; the VSIX is installed into the driven version's hives with its own VSIXInstaller.
.PARAMETER CloudProviderId
    A configured cloud provider whose key is stored; its first model is measured for the cloud Fast autocomplete and chat
    first token rows. Without it (or without a stored key) those rows are pending.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipLiveTests,
    [switch]$SkipIde,
    [switch]$SkipCost,
    [switch]$KeepInstanceOpen,
    [string]$DevEnvPath,
    [string]$CloudProviderId,
    [string]$ProviderId = 'ollama',
    [string]$StrongModel = 'qwen2.5-coder:7b',
    [string]$FastModel = 'qwen2.5-coder:1.5b',
    [string]$OllamaEndpoint = 'http://localhost:11434',
    [string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) 'pillion-gate1'),
    [string]$CostRootSuffix = 'PillionG1',
    [int]$CostPairs = 2,
    [int]$MetricsWindowSec = 30,
    [int]$WaitForDevenvSec = 3600
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repo 'tools\gate0\Gate0.Checks.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Gate1.Scenarios.psm1') -Force

$runDir = Initialize-Gate0Run -RepoRoot $repo -Label 'gate1' -OutRoot 'tools/gate1/out'
New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
$runStart = [DateTimeOffset]::Now
$run = [ordered]@{
    kind       = 'Invoke-Gate1'
    startedAt  = $runStart.ToString('o')
    machine    = $env:COMPUTERNAME
    parameters = [ordered]@{ SkipBuild = [bool]$SkipBuild; SkipTests = [bool]$SkipTests; SkipLiveTests = [bool]$SkipLiveTests; SkipIde = [bool]$SkipIde; SkipCost = [bool]$SkipCost; KeepInstanceOpen = [bool]$KeepInstanceOpen; DevEnvPath = $DevEnvPath; CloudProviderId = $CloudProviderId; ProviderId = $ProviderId; StrongModel = $StrongModel; FastModel = $FastModel; CostRootSuffix = $CostRootSuffix; CostPairs = $CostPairs }
    versions   = [ordered]@{}
    data       = [ordered]@{}
}
$steps = [System.Collections.Generic.List[object]]::new()
$data = $run.data

function Add-PendingStep([string]$Name, [string]$Summary) {
    $steps.Add([pscustomobject]@{ name = $Name; status = 'Pending'; startedAt = (Get-Date).ToString('o'); durationMs = 0; details = [ordered]@{ Summary = $Summary }; error = $null })
}

function Add-SkippedStep([string]$Name, [string]$Summary) {
    $steps.Add([pscustomobject]@{ name = $Name; status = 'Skipped'; startedAt = (Get-Date).ToString('o'); durationMs = 0; details = [ordered]@{ Summary = $Summary }; error = $null })
}

# Prerequisites.
$vs = $null
$pre = Invoke-Gate0Step -Name 'Prerequisites' -Body {
    $p = Assert-Gate0Prerequisites -Model $StrongModel -OllamaEndpoint $OllamaEndpoint -DevEnvPath $DevEnvPath -RootSuffix $CostRootSuffix
    $fast = Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $FastModel
    if (-not $fast.ModelPulled) { throw "The model '$FastModel' is not pulled in Ollama (installed: $($fast.Models -join ', '))." }
    $script:vs = $p.VisualStudio
    $script:ollamaVersion = $p.Ollama.Version
    [ordered]@{ Summary = "$($p.VisualStudio.DisplayName) $($p.VisualStudio.Version) (build: $($p.VisualStudio.BuildDisplayName)); Ollama $($p.Ollama.Version) with $StrongModel and $FastModel" }
}
$steps.Add($pre)
if ($pre.status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title 'Gate 1 evidence run' -FilePrefix 'gate1'
    Write-Error "Prerequisites not met: $($pre.error)" -ErrorAction Continue
    exit 2
}
$run.versions['Visual Studio'] = "$($vs.DisplayName) $($vs.Version) ($($vs.InstallationVersion))"
$run.versions['MSBuild'] = $vs.BuildDisplayName
$run.versions['Ollama'] = "$ollamaVersion at $OllamaEndpoint"
$run.versions['Machine'] = Get-MachineDescription
$run.versions['PowerShell'] = $PSVersionTable.PSVersion.ToString()
try { $run.versions['git commit'] = (& git -C $repo rev-parse HEAD 2>$null) + ' (' + (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null) + ')' } catch { }
try { $run.versions['.NET SDK'] = (& dotnet --version 2>$null) } catch { }

# a. Build and versions.
$steps.Add((Invoke-Gate0Step -Name 'a. Build and versions' -Body {
            $d = [ordered]@{}
            if ($SkipBuild) { $d['build'] = 'skipped (-SkipBuild)' }
            else {
                $buildLog = Join-Path $runDir 'build.log'
                Push-Location $repo
                try {
                    & $vs.MSBuild PillionProgrammer.sln /restore /p:Configuration=Release /m /p:DeployExtension=false /nologo /v:minimal /clp:Summary 2>&1 | Out-File -LiteralPath $buildLog -Encoding utf8
                    $code = $LASTEXITCODE
                }
                finally { Pop-Location }
                $d['build'] = "MSBuild ($($vs.BuildDisplayName)) exit code $code; DeployExtension=false; log $buildLog"
                $d['buildWarningsErrors'] = (Select-String -LiteralPath $buildLog -Pattern '^\s+\d+ (Warning|Error)\(s\)' | ForEach-Object { $_.Line.Trim() }) -join ', '
                if ($code -ne 0) { throw "The build failed (exit code $code); see $buildLog" }
            }
            $vsix = Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
            if (-not (Test-Path $vsix)) { throw "No VSIX at $vsix; build first (run without -SkipBuild)." }
            $d['vsixManifestVersion'] = Get-VsixManifestVersion -VsixPath $vsix
            $d['vsixSha256'] = (Get-FileHash $vsix).Hash
            $hostExe = Join-Path $repo 'src\Pillion.Host\bin\Release\net10.0\Pillion.Host.exe'
            $d['hostVersion'] = if (Test-Path $hostExe) { Get-HostVersionText -HostExe $hostExe } else { '(Pillion.Host.exe not built)' }
            $d['ollamaVersion'] = (Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $StrongModel).Version
            $d['models'] = Get-OllamaModelDigests -Endpoint $OllamaEndpoint
            $run.versions['VSIX manifest'] = $d['vsixManifestVersion']
            $run.versions['Pillion.Host --version'] = $d['hostVersion']
            foreach ($m in @($d['models'].installed | Where-Object { $_.name -in $StrongModel, $FastModel })) { $run.versions["Model $($m.name)"] = "digest $($m.digest.Substring(0, 12)), $($m.parameters), $($m.quantization)" }
            $d['Summary'] = "VSIX $($d['vsixManifestVersion']); $($d['hostVersion']); Ollama $($d['ollamaVersion']); models " + ((@($d['models'].installed | Where-Object { $_.name -in $StrongModel, $FastModel }) | ForEach-Object { "$($_.name) $($_.digest.Substring(0, 12))" }) -join ', ')
            $d
        }))
if ($steps[-1].status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title 'Gate 1 evidence run' -FilePrefix 'gate1'
    Write-Error 'The build failed; see the run folder.' -ErrorAction Continue
    exit 1
}

# b. Tests.
$unit = $null
$live = $null
if ($SkipTests) { Add-SkippedStep 'b1. Unit and component tests' 'skipped (-SkipTests)'; Add-SkippedStep 'b2. Live tests (Ollama)' 'skipped (-SkipTests)' }
else {
    $steps.Add((Invoke-Gate0Step -Name 'b1. Unit and component tests (Category!=Live)' -Body {
                $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets @('PillionProgrammer.NoVsix.slnf') -Filter 'Category!=Live' -ResultsDir (Join-Path $runDir 'tests-unit') -LogPath (Join-Path $runDir 'test-unit.log') -NoBuild
                $script:unit = $r
                $perProject = ($r.Projects.Keys | ForEach-Object { '{0} {1}/{2}/{3}' -f $_, $r.Projects[$_].passed, $r.Projects[$_].failed, $r.Projects[$_].skipped }) -join '; '
                [ordered]@{
                    Status   = (@($r.ExitCodes | Where-Object { $_ -ne 0 }).Count -eq 0 -and $r.Totals.failed -eq 0 -and $r.Totals.passed -gt 0) ? 'Pass' : 'Fail'
                    Summary  = "passed $($r.Totals.passed), failed $($r.Totals.failed), skipped $($r.Totals.skipped) (passed/failed/skipped: $perProject)"
                    projects = $r.Projects
                    failed   = $r.Failed
                    skipped  = $r.Skipped
                }
            }))
    if ($SkipLiveTests) { Add-SkippedStep 'b2. Live tests (Ollama)' 'skipped (-SkipLiveTests)' }
    else {
        $steps.Add((Invoke-Gate0Step -Name 'b2. Live tests (Category=Live, Ollama)' -Body {
                    # The live tests load their own models (gemma4:e2b for chat, the coder models for FIM, the 1.5B on the CPU)
                    # and do not fit beside pinned models on an 8 GB GPU: start them from an idle Ollama. The strong model is
                    # made resident again at the end of the run (step n), and the IDE scenarios warm their models up.
                    $unloaded = @(Clear-OllamaResidentModels -Endpoint $OllamaEndpoint)
                    $targets = @('tests\Pillion.ModelGateway.Tests\Pillion.ModelGateway.Tests.csproj', 'tests\Pillion.Host.Tests\Pillion.Host.Tests.csproj')
                    $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets $targets -Filter 'Category=Live' -ResultsDir (Join-Path $runDir 'tests-live') -LogPath (Join-Path $runDir 'test-live.log') -NoBuild -Serial
                    $script:live = $r
                    $perProject = ($r.Projects.Keys | ForEach-Object { '{0} {1}/{2}/{3}' -f $_, $r.Projects[$_].passed, $r.Projects[$_].failed, $r.Projects[$_].skipped }) -join '; '
                    [ordered]@{
                        Status  = ($r.Totals.failed -eq 0 -and $r.Totals.passed -gt 0) ? 'Pass' : 'Fail'
                        Summary = "passed $($r.Totals.passed), failed $($r.Totals.failed), skipped $($r.Totals.skipped) (cloud live tests skip without keys) ($perProject); unloaded first: $(if ($unloaded.Count) { $unloaded -join ', ' } else { 'nothing was resident' })"
                        unloadedBefore = $unloaded
                        failed  = $r.Failed
                        skipped = $r.Skipped
                    }
                }))
    }
    $data['measurementsFromTests'] = Get-LiveMeasurements -LiveResults $(if ($live) { $live.Results } else { @() }) -UnitResults $(if ($unit) { $unit.Results } else { @() })
}

# c to n, with settings restored whatever happens.
$ide = $null
$cost = $null
$cloud = $null
try {
    $steps.Add((Invoke-Gate0Step -Name 'c. Back up and prepare settings.json' -Body {
                $b = Backup-PillionSettings
                $settings = Read-PillionSettings
                if ($null -eq $settings) { $settings = New-DefaultPillionSettings }
                $providers = [System.Collections.Generic.List[object]]::new()
                foreach ($p in @($settings['providers'])) { if ($null -ne $p) { $providers.Add($p) } }
                $local = $providers | Where-Object { $_['id'] -eq $ProviderId } | Select-Object -First 1
                if ($null -eq $local) {
                    $local = [ordered]@{ id = $ProviderId; kind = 'ollama'; displayName = 'Ollama'; endpoint = $OllamaEndpoint; models = @($StrongModel, $FastModel); enabled = $true }
                    $providers.Insert(0, $local)
                }
                elseif ($local['kind'] -ne 'ollama') { throw "Provider '$ProviderId' in settings.json has kind '$($local['kind'])', not ollama." }
                else {
                    $local['enabled'] = $true
                    $local['models'] = @(@($local['models']) + $StrongModel + $FastModel | Where-Object { $_ } | Select-Object -Unique)
                }
                $settings['providers'] = $providers.ToArray()
                foreach ($section in 'routing', 'features', 'privacy') { if (-not $settings.Contains($section) -or $null -eq $settings[$section]) { $settings[$section] = [ordered]@{} } }
                foreach ($section in 'tasks', 'profiles') { if (-not $settings['routing'].Contains($section) -or $null -eq $settings['routing'][$section]) { $settings['routing'][$section] = [ordered]@{} } }
                $settings['routing']['tasks']['autocomplete'] = 'localFast'
                $settings['routing']['tasks']['inlineEdit'] = 'localStrong'
                $settings['routing']['tasks']['chat'] = 'localStrong'
                $settings['routing']['profiles']['localFast'] = "$ProviderId/$FastModel"
                $settings['routing']['profiles']['localStrong'] = "$ProviderId/$StrongModel"
                foreach ($flag in 'chat', 'modelGateway', 'ollamaProvider', 'autocomplete', 'inlineChat', 'mentions', 'outboundLogView') { $settings['features'][$flag] = $true }
                $settings['privacy']['localOnly'] = $false
                Write-PillionSettings -Settings $settings
                [ordered]@{ Summary = "backup sha256 $($b.Sha256) (existed: $($b.Existed)); provider '$ProviderId' with $StrongModel (Local-strong: chat, inline edits) and $FastModel (Local-fast: autocomplete); phase 1 flags on"; backupSha256 = $b.Sha256 }
            }))

    # Cloud: only with a stored key.
    $cloudEnv = @{}
    if ($CloudProviderId) {
        if (-not (Test-CloudKeyStored -ProviderId $CloudProviderId)) { $cloud = "pending: no key stored for PillionProgrammer/$CloudProviderId" }
        else {
            $provider = Get-PillionProviderSettings -Id $CloudProviderId
            if (-not $provider) { $cloud = "pending: provider '$CloudProviderId' is not in settings.json" }
            else { $cloudEnv['PILLION_GATE_CLOUD_PROVIDER'] = $CloudProviderId; $cloudEnv['PILLION_GATE_CLOUD_MODEL'] = @($provider['models'])[0]; $cloud = "measured with $CloudProviderId/$(@($provider['models'])[0])" }
        }
    }
    else { $cloud = 'pending: no cloud key stored (run with -CloudProviderId <id> after storing one)' }
    $data['cloud'] = $cloud

    if ($SkipIde) { Add-SkippedStep 'd-k. IDE run (integration tests and Gate 1 scenarios)' 'skipped (-SkipIde)' }
    else {
        $steps.Add((Invoke-Gate0Step -Name "d-k. IDE run in the Experimental Instance (VS$($vs.Major)): integration tests and Gate 1 scenarios" -Body {
                    $envVars = @{ PILLION_METRICS_WINDOW_SECONDS = "$MetricsWindowSec"; PILLION_GATE_PROVIDER = $ProviderId; PILLION_GATE_STRONG_MODEL = $StrongModel; PILLION_GATE_FAST_MODEL = $FastModel }
                    foreach ($k in $cloudEnv.Keys) { $envVars[$k] = $cloudEnv[$k] }
                    $r = Invoke-GateIdeRun -RepoRoot $repo -VisualStudio $vs -RunDir $runDir -ScenarioEnvironment $envVars -WaitForDevenvSec $WaitForDevenvSec
                    $script:ide = $r
                    $failed = @($r.Results | Where-Object Outcome -EQ 'Failed')
                    $skipped = @($r.Results | Where-Object { $_.Outcome -notin 'Passed', 'Failed' })
                    # The metrics log lines written during the run.
                    $metricsLines = @()
                    foreach ($f in @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'PillionProgrammer\logs') -Filter 'metrics-*.jsonl' -ErrorAction SilentlyContinue)) {
                        foreach ($line in (Read-SharedLines $f.FullName)) {
                            try { $o = $line | ConvertFrom-Json -DateKind String } catch { continue }
                            if ([DateTimeOffset]::Parse($o.windowEnd, [Globalization.CultureInfo]::InvariantCulture) -ge $r.Started) { $metricsLines += $line }
                        }
                    }
                    $metricsPath = Join-Path $r.ResultsDir 'metrics-during-run.jsonl'
                    Set-Content -LiteralPath $metricsPath -Value $metricsLines -Encoding utf8
                    [ordered]@{
                        Status          = ($r.ExitCode -eq 0 -and $failed.Count -eq 0 -and $r.Results.Count -gt 0) ? 'Pass' : 'Fail'
                        Summary         = "$(@($r.Results | Where-Object Outcome -EQ 'Passed').Count) passed, $($failed.Count) failed, $($skipped.Count) skipped; scenarios: $($r.Scenarios.Keys -join ', '); waited $($r.WaitedForDevenvSec) s for devenv.exe; $($r.Install)"
                        trx             = $r.Trx
                        log             = $r.Log
                        deployedTo      = $r.DeployedTo
                        tests           = @($r.Results | ForEach-Object { "$($_.Outcome) $($_.Name) ($($_.Duration))" })
                        failed          = @($failed | ForEach-Object { "$($_.Name): $($_.Error)" })
                        packageLoad     = $r.PackageLoad
                        hostKill        = $r.HostKill
                        metricsLogLines = $metricsLines.Count
                        metricsLog      = $metricsPath
                    }
                }))
        if ($ide) { $data['ide'] = [ordered]@{ label = $ide.Label; scenarios = $ide.Scenarios; packageLoad = $ide.PackageLoad; hostKill = $ide.HostKill; results = @($ide.Results | ForEach-Object { [ordered]@{ name = $_.Name; outcome = $_.Outcome } }) } }
    }

    if ($SkipCost) { Add-SkippedStep "g. VSIX cost (/rootsuffix $CostRootSuffix against ${CostRootSuffix}Off)" 'skipped (-SkipCost)' }
    else {
        $steps.Add((Invoke-Gate0Step -Name "g. VSIX cost: /rootsuffix $CostRootSuffix (on) against ${CostRootSuffix}Off (off)" -Body {
                    $c = Measure-VsixCost -RepoRoot $repo -VisualStudio $vs -ScratchRoot $ScratchRoot -OnSuffix $CostRootSuffix -OffSuffix "${CostRootSuffix}Off" -Pairs $CostPairs -KeepInstalled:$KeepInstanceOpen -LeaveLastOpen:$KeepInstanceOpen -WaitForDevenvSec $WaitForDevenvSec
                    $script:cost = $c
                    $d = [ordered]@{ Summary = "UI thread CPU in the first 30 s: on $($c.uiCpuMedianOnMs) ms, off $($c.uiCpuMedianOffMs) ms (delta $($c.uiCpuDeltaMs) ms); working set after 60 s: on $($c.workingSetMedianOnMB) MB, off $($c.workingSetMedianOffMB) MB (delta $($c.workingSetDeltaMB) MB); hosts started in $($c.hostsStartedWhenOn) of $CostPairs extension-on launches; uninstalled: $($c.uninstalled)" }
                    foreach ($k in $c.Keys) { $d[$k] = $c[$k] }
                    $d['Status'] = ($c.hostsStartedWhenOn -eq $CostPairs) ? 'Pass' : 'Fail'
                    $d
                }))
        if ($cost) { $data['cost'] = $cost }
    }

    $steps.Add((Invoke-Gate0Step -Name 'j. Degradation (component test, real pipe, slow fake host)' -Body {
                $fromTests = $data['measurementsFromTests']
                $lines = if ($fromTests) { @($fromTests['degradationTest']) } else { @() }
                if ($lines.Count -eq 0) { return [ordered]@{ Status = 'Skipped'; Summary = 'no test results (step b skipped)' } }
                $bad = @($lines | Where-Object { $_ -notmatch ': Passed$' })
                [ordered]@{ Status = ($bad.Count -eq 0) ? 'Pass' : 'Fail'; Summary = ($lines -join '; '); tests = $lines }
            }))
    Add-PendingStep 'l. Completion evaluation baseline' 'pending the product owner: the private golden-set repository (work item 1.17) does not exist yet'
    $steps.Add((Invoke-Gate0Step -Name 'm. CI on develop' -Body {
                $ci = Get-CiRuns
                if (-not $ci) { return [ordered]@{ Status = 'Pending'; Summary = 'gh is not available or returned nothing' } }
                $data['ci'] = $ci
                $latest = $ci.runs[0]
                [ordered]@{
                    Status  = ($latest.conclusion -eq 'success') ? 'Pass' : (($latest.status -ne 'completed') ? 'Pending' : 'Fail')
                    Summary = "latest develop run $($latest.url) ($($latest.sha), $($latest.status) $($latest.conclusion)); jobs: " + (($ci.latestJobs | ForEach-Object { "$($_.name) $($_.conclusion)" }) -join '; ')
                    runs    = $ci.runs
                    jobs    = $ci.latestJobs
                }
            }))
}
finally {
    $steps.Add((Invoke-Gate0Step -Name 'n. Restore settings, check processes, leave the strong model resident' -Body {
                $d = [ordered]@{}
                $d['logExcerpts'] = Save-LogExcerpts -Since $runStart
                $r = Restore-PillionSettings
                $d['settingsRestored'] = $r
                # Anything this run left: devenv with the run's root suffixes (unless kept open on purpose), and their hosts.
                $suffixes = @($CostRootSuffix, "${CostRootSuffix}Off")
                $left = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object { $cl = $_.CommandLine; @($suffixes | Where-Object { $cl -match "/rootsuffix\s+`"?$([regex]::Escape($_))\b" }).Count -gt 0 } | Where-Object { -not ($cost -and $cost.leftOpenPid -eq $_.ProcessId) })
                $d['devenvLeft'] = @($left | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" })
                foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
                $deployedInCostHive = $null
                if ($vs) { $deployedInCostHive = [bool](Find-DeployedExtension -ExpHive (Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$($vs.Major).0_$($vs.InstanceId)$CostRootSuffix")) }
                $d['extensionFolderInCostHive'] = $deployedInCostHive
                # Leave the strong model resident (keep_alive -1), as the developer's machine had it.
                # Only when it is not resident already: a load request with another context size would make Ollama wait for
                # a second instance that does not fit beside the first (decision log, 2026-10-10), so the load asks for the
                # 4,096 tokens Pillion's residency gives the 7B model on this 8 GB tier.
                try {
                    $resident = @((Invoke-RestMethod -Uri "$OllamaEndpoint/api/ps" -TimeoutSec 10).models | ForEach-Object { $_.name })
                    if ($resident -notcontains $StrongModel) {
                        $body = @{ model = $StrongModel; keep_alive = -1; options = @{ num_ctx = 4096 } } | ConvertTo-Json
                        Invoke-RestMethod -Uri "$OllamaEndpoint/api/generate" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 180 | Out-Null
                    }
                    $d['residentAtEnd'] = @((Invoke-RestMethod -Uri "$OllamaEndpoint/api/ps" -TimeoutSec 10).models | ForEach-Object { "$($_.name) (expires $($_.expires_at))" })
                }
                catch { $d['residentAtEnd'] = "could not ask Ollama: $($_.Exception.Message)" }
                $bad = ($left.Count -gt 0) -or ($r.Restored -and -not $r.MatchesBackup) -or -not ((@($d['residentAtEnd']) -join ' ') -match [regex]::Escape($StrongModel))
                $d['Status'] = $bad ? 'Fail' : 'Pass'
                $d['Summary'] = "settings.json restored, matches backup: $($r.MatchesBackup); devenv left: $($left.Count); Pillion folder still in the $CostRootSuffix hive: $deployedInCostHive (Visual Studio deletes an uninstalled extension's folder at its next start); resident: $($d['residentAtEnd'] -join ', ')"
                $d
            }))
    $table = New-Gate1Table -Data $data -Steps $steps.ToArray() -VisualStudio $vs -StrongModel $StrongModel -FastModel $FastModel
    $run['gate1Table'] = $table.Rows
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title "Gate 1 evidence run (VS$($vs.Major))" -FilePrefix 'gate1' -Preamble $table.Markdown
}

$failedSteps = @($steps | Where-Object status -EQ 'Fail')
$pendingSteps = @($steps | Where-Object status -EQ 'Pending')
Write-Gate0Log ("Done: {0} steps, {1} failed, {2} pending. Results in {3}" -f $steps.Count, $failedSteps.Count, $pendingSteps.Count, $runDir) -Level ($failedSteps.Count ? 'fail' : 'pass')
exit ($failedSteps.Count ? 1 : 0)
