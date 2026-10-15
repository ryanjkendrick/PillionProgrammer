#Requires -Version 7.5
<#
.SYNOPSIS
    Collects the Gate 2 evidence (work item 2.17): build and versions, the unit and component tests, the Benchmark and Model
    tests, the generated-repository indexing runs (100,000 and 1,000,000 lines, CPU and DirectML), the IDE scenarios in the
    Experimental Instance (with the Gate 1 scenarios re-run with retrieval on), encryption at rest, two instances on one
    index, the retrieval evaluation row, CI, flags, and the Gate 2 criteria table.

.DESCRIPTION
    Run from a plain PowerShell 7 prompt in the repository root, with Ollama running, the Gate 1 models pulled, git on PATH,
    Python on PATH (for the plain-SQLite check) and no Visual Studio open:

        pwsh tools/gate2/Invoke-Gate2.ps1
        pwsh tools/gate2/Invoke-Gate2.ps1 -SkipBuild -SkipBenchmarks -SkipIndexing      # IDE scenarios only (needs the big index)
        pwsh tools/gate2/Invoke-Gate2.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"

    Steps, each timed and logged to tools/gate2/out/<timestamp>-gate2/ (git-ignored):
      a. Build the full solution (Release, Visual Studio 2026's MSBuild, no deployment); versions of the VSIX and the host,
         the bundled model files by SHA-256, the Ollama models by digest.
      b. Unit and component tests (Category!=Live&Category!=Benchmark&Category!=Model) with TRX files.
      c. The Benchmark and Model tests one project at a time from an idle machine (Ollama models unloaded first), with the
         figures they print.
      d. Generated repositories indexed by the real pipeline with the bundled model (Gate2IndexingTests): 100,000 lines
         (synthetic C# and real code) and 1,000,000 lines (synthetic and real), on the CPU, and with DirectML too while issue
         #78 is open. The synthetic 1,000,000-line run indexes into the real data folder, so the IDE scenarios find a
         100,000-chunk index ready; step i deletes it.
      e. The IDE scenarios: settings.json backed up and set for the run (Ollama Local-strong and Local-fast as in Gate 1, every
         phase 1 and phase 2 flag on), the generated solution created (git, a realistic-diff and a whole-file-rewrite branch
         of 500 files, two secret-bearing files), then the whole IDE suite with PILLION_GATE_SCENARIOS=1 and
         PILLION_GATE2_SCENARIOS=1 (Gate 1 scenarios with retrieval on, then the Gate 2 ones), then a second Visual Studio
         session for the restart check.
      e2. Encryption at rest: index.db and conversations.db opened with plain SQLite (a copy; must fail), the key files'
         ACLs, and a search of the data folder for a sentence that only the generated code holds.
      e3. Two instances: the generated solution opened in two Visual Studio instances of a private hive; one writer.lock
         owner; the owner killed; the other takes over.
      f. The retrieval row from docs/eval-retrieval-baseline.md (quoted, not re-run).
      g. CI on develop (gh).
      h. Feature flags: the README flag list and the test results.
      i. settings.json restored byte for byte, the gate's indexes deleted, the private hive cleaned, processes checked.

.PARAMETER KeepIndexes
    Leave the gate's indexes in the data folder at the end (the big index of step d), so a run for the other Visual Studio
    version can use -SkipIndexing. The last run of the day should not pass it.
.PARAMETER TestCaseFilter
    An extra vstest filter for the IDE run (for example "FullyQualifiedName~Gate2_" while working on the scenarios).
.PARAMETER Quick
    A smoke run of the script: 20,000 and 50,000 lines instead of 100,000 and 1,000,000, no real-code 1,000,000 run.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipBenchmarks,
    [switch]$SkipIndexing,
    [switch]$SkipIde,
    [switch]$SkipTwoInstances,
    [switch]$Quick,
    [switch]$KeepIndexes,
    [ValidateSet('auto', 'yes', 'no')][string]$DirectML = 'auto',
    [string]$DevEnvPath,
    [string]$TestCaseFilter,
    [string]$ProviderId = 'ollama',
    [string]$StrongModel = 'qwen2.5-coder:7b',
    [string]$FastModel = 'qwen2.5-coder:1.5b',
    [string]$OllamaEndpoint = 'http://localhost:11434',
    [string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) 'pillion-gate2'),
    [string]$TwoInstanceSuffix = 'PillionG2',
    [int]$MetricsWindowSec = 30,
    [int]$WaitForDevenvSec = 3600
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repo 'tools\gate0\Gate0.Checks.psm1') -Force
Import-Module (Join-Path $repo 'tools\gate1\Gate1.Scenarios.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Gate2.Scenarios.psm1') -Force

$runDir = Initialize-Gate0Run -RepoRoot $repo -Label 'gate2' -OutRoot 'tools/gate2/out'
New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
$runStart = [DateTimeOffset]::Now
$run = [ordered]@{
    kind       = 'Invoke-Gate2'
    startedAt  = $runStart.ToString('o')
    machine    = $env:COMPUTERNAME
    parameters = [ordered]@{ SkipBuild = [bool]$SkipBuild; SkipTests = [bool]$SkipTests; SkipBenchmarks = [bool]$SkipBenchmarks; SkipIndexing = [bool]$SkipIndexing; SkipIde = [bool]$SkipIde; SkipTwoInstances = [bool]$SkipTwoInstances; Quick = [bool]$Quick; DirectML = $DirectML; DevEnvPath = $DevEnvPath; StrongModel = $StrongModel; FastModel = $FastModel; ScratchRoot = $ScratchRoot }
    versions   = [ordered]@{}
    data       = [ordered]@{}
}
$steps = [System.Collections.Generic.List[object]]::new()
$data = $run.data
$smallLines = if ($Quick) { 20000 } else { 100000 }
$bigLines = if ($Quick) { 50000 } else { 1000000 }
$bigRepo = Join-Path $ScratchRoot "repo-synthetic-$bigLines"

function Add-StatusStep([string]$Name, [string]$Status, [string]$Summary) {
    $steps.Add([pscustomobject]@{ name = $Name; status = $Status; startedAt = (Get-Date).ToString('o'); durationMs = 0; details = [ordered]@{ Summary = $Summary }; error = $null })
}

# Prerequisites.
$vs = $null
$pre = Invoke-Gate0Step -Name 'Prerequisites' -Body {
    $p = Assert-Gate0Prerequisites -Model $StrongModel -OllamaEndpoint $OllamaEndpoint -DevEnvPath $DevEnvPath -RootSuffix $TwoInstanceSuffix
    $fast = Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $FastModel
    if (-not $fast.ModelPulled) { throw "The model '$FastModel' is not pulled in Ollama." }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is not on PATH (the generated solution is a git repository).' }
    $script:vs = $p.VisualStudio
    $script:ollamaVersion = $p.Ollama.Version
    [ordered]@{ Summary = "$($p.VisualStudio.DisplayName) $($p.VisualStudio.Version) (build: $($p.VisualStudio.BuildDisplayName)); Ollama $($p.Ollama.Version); git $((& git --version) -replace 'git version ', '')" }
}
$steps.Add($pre)
if ($pre.status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title 'Gate 2 evidence run' -FilePrefix 'gate2'
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
$issue78Open = $false
try { $issue78Open = ((& gh issue view 78 --repo ryanjkendrick/PillionProgrammer --json state 2>$null | ConvertFrom-Json).state -eq 'OPEN') } catch { }
$runDirectML = switch ($DirectML) { 'yes' { $true } 'no' { $false } default { $issue78Open } }
$data['issue78Open'] = $issue78Open

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
                $d['build'] = "MSBuild ($($vs.BuildDisplayName)) exit code $code; log $buildLog"
                $d['buildWarningsErrors'] = (Select-String -LiteralPath $buildLog -Pattern '^\s+\d+ (Warning|Error)\(s\)' | ForEach-Object { $_.Line.Trim() }) -join ', '
                if ($code -ne 0) { throw "The build failed (exit code $code); see $buildLog" }
            }
            $vsix = Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
            if (-not (Test-Path $vsix)) { throw "No VSIX at $vsix; build first." }
            $d['vsixManifestVersion'] = Get-VsixManifestVersion -VsixPath $vsix
            $d['vsixSha256'] = (Get-FileHash $vsix).Hash
            $d['vsixMB'] = [math]::Round((Get-Item $vsix).Length / 1MB, 1)
            $hostExe = Join-Path $repo 'src\Pillion.Host\bin\Release\net10.0\Pillion.Host.exe'
            $d['hostVersion'] = if (Test-Path $hostExe) { Get-HostVersionText -HostExe $hostExe } else { '(Pillion.Host.exe not built)' }
            $models = Join-Path $repo 'src\Pillion.Host\bin\Release\net10.0\models'
            $d['bundledModelFiles'] = @(Get-ChildItem -LiteralPath $models -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { "$([IO.Path]::GetRelativePath($models, $_.FullName)) sha256 $((Get-FileHash $_.FullName).Hash.ToLowerInvariant()) ($($_.Length) bytes)" })
            $d['ollamaModels'] = Get-OllamaModelDigests -Endpoint $OllamaEndpoint
            $run.versions['VSIX manifest'] = $d['vsixManifestVersion']
            $run.versions['Pillion.Host --version'] = $d['hostVersion']
            foreach ($f in $d['bundledModelFiles']) { if ($f -like '*.onnx*') { $run.versions['Bundled model'] = $f } }
            foreach ($m in @($d['ollamaModels'].installed | Where-Object { $_.name -in $StrongModel, $FastModel })) { $run.versions["Model $($m.name)"] = "digest $($m.digest.Substring(0, 12)), $($m.parameters), $($m.quantization)" }
            $d['Summary'] = "VSIX $($d['vsixManifestVersion']) ($($d['vsixMB']) MB); $($d['hostVersion']); bundled model files $($d['bundledModelFiles'].Count); issue #78 open: $issue78Open"
            $d
        }))
if ($steps[-1].status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title 'Gate 2 evidence run' -FilePrefix 'gate2'
    exit 1
}

# b. Unit and component tests.
if ($SkipTests) { Add-StatusStep 'b. Unit and component tests' 'Skipped' 'skipped (-SkipTests)' }
else {
    $steps.Add((Invoke-Gate0Step -Name 'b. Unit and component tests (Category!=Live&Category!=Benchmark&Category!=Model)' -Body {
                $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets @('PillionProgrammer.NoVsix.slnf') -Filter 'Category!=Live&Category!=Benchmark&Category!=Model' -ResultsDir (Join-Path $runDir 'tests-unit') -LogPath (Join-Path $runDir 'test-unit.log') -NoBuild
                $perProject = ($r.Projects.Keys | ForEach-Object { '{0} {1}/{2}/{3}' -f $_, $r.Projects[$_].passed, $r.Projects[$_].failed, $r.Projects[$_].skipped }) -join '; '
                $data['unit'] = [ordered]@{ totals = $r.Totals; projects = $r.Projects; failed = $r.Failed }
                $data['unitMeasurements'] = Get-LiveMeasurements -UnitResults $r.Results
                [ordered]@{
                    Status  = (@($r.ExitCodes | Where-Object { $_ -ne 0 }).Count -eq 0 -and $r.Totals.failed -eq 0 -and $r.Totals.passed -gt 0) ? 'Pass' : 'Fail'
                    Summary = "passed $($r.Totals.passed), failed $($r.Totals.failed), skipped $($r.Totals.skipped) ($perProject)"
                    failed  = $r.Failed
                }
            }))
}

# c. Benchmark and Model tests, one project at a time from an idle machine.
if ($SkipBenchmarks) { Add-StatusStep 'c. Benchmark and Model tests' 'Skipped' 'skipped (-SkipBenchmarks)' }
else {
    $steps.Add((Invoke-Gate0Step -Name 'c. Benchmark and Model tests (one project at a time, idle machine)' -Body {
                $unloaded = @(Clear-OllamaResidentModels -Endpoint $OllamaEndpoint)
                $targets = @(
                    'tests\Pillion.LocalStore.Tests\Pillion.LocalStore.Tests.csproj',
                    'tests\Pillion.ContextEngine.Tests\Pillion.ContextEngine.Tests.csproj',
                    'tests\Pillion.ModelGateway.Tests\Pillion.ModelGateway.Tests.csproj',
                    'tests\Pillion.Host.Tests\Pillion.Host.Tests.csproj',
                    'tests\Pillion.Eval.Tests\Pillion.Eval.Tests.csproj')
                $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets $targets -Filter '(Category=Benchmark|Category=Model)&Category!=Live&Category!=Gate2' -ResultsDir (Join-Path $runDir 'tests-benchmark') -LogPath (Join-Path $runDir 'test-benchmark.log') -NoBuild -Serial
                $data['benchmarks'] = [ordered]@{ totals = $r.Totals; projects = $r.Projects; failed = $r.Failed; figures = Get-Gate2BenchmarkFigures -Results $r.Results; all = @($r.Results | ForEach-Object { [ordered]@{ name = $_.Name; outcome = $_.Outcome; duration = $_.Duration; lines = @(($_.StdOut -split "`r?`n") | Where-Object { $_ -and $_ -notmatch '^\d\d:\d\d:\d\d\.\d+ (info|dbug|warn|fail|trce):' } | Select-Object -First 40) } }) }
                [ordered]@{
                    Status  = ($r.Totals.failed -eq 0 -and $r.Totals.passed -gt 0) ? 'Pass' : 'Fail'
                    Summary = "passed $($r.Totals.passed), failed $($r.Totals.failed), skipped $($r.Totals.skipped); unloaded first: $(if ($unloaded.Count) { $unloaded -join ', ' } else { 'nothing was resident' })"
                    failed  = $r.Failed
                }
            }))
}

# d. Generated repositories.
$indexRuns = [System.Collections.Generic.List[object]]::new()
if ($SkipIndexing) { Add-StatusStep 'd. Generated repositories' 'Skipped' 'skipped (-SkipIndexing)' }
else {
    $plan = @(
        @{ Source = 'synthetic'; Lines = $smallLines; Ep = 'cpu' },
        @{ Source = 'real'; Lines = $smallLines; Ep = 'cpu' },
        @{ Source = 'synthetic'; Lines = $bigLines; Ep = 'cpu'; Data = (Join-Path $env:LOCALAPPDATA 'PillionProgrammer'); Repo = $bigRepo }
    )
    if (-not $Quick) { $plan += @{ Source = 'real'; Lines = $bigLines; Ep = 'cpu' } }
    if ($runDirectML) {
        $plan += @{ Source = 'synthetic'; Lines = $smallLines; Ep = 'directml' }
        $plan += @{ Source = 'real'; Lines = $smallLines; Ep = 'directml' }
        if (-not $Quick) { $plan += @{ Source = 'real'; Lines = $bigLines; Ep = 'directml' } }
    }
    foreach ($p in $plan) {
        $name = "d. Index $($p.Lines) lines ($($p.Source), $($p.Ep))"
        $steps.Add((Invoke-Gate0Step -Name $name -Body {
                    $label = "index-$($p.Source)-$($p.Lines)-$($p.Ep)"
                    $json = Join-Path $runDir "$label.json"
                    $repoFolder = if ($p.Repo) { $p.Repo } else { Join-Path $ScratchRoot "repo-$($p.Source)-$($p.Lines)" }
                    if ($p.Data) {
                        # The big synthetic index lives in the real data folder for the IDE scenarios: start from a clean one.
                        $removed = Remove-Gate2Indexes -UnderPath $repoFolder
                        if ($removed) { Write-Gate0Log "Removed an earlier gate index: $($removed -join '; ')" }
                    }
                    [void](Clear-OllamaResidentModels -Endpoint $OllamaEndpoint)
                    $envVars = @{ PILLION_GATE2_INDEX = '1'; PILLION_GATE2_LINES = "$($p.Lines)"; PILLION_GATE2_SOURCE = $p.Source; PILLION_GATE2_EP = $p.Ep; PILLION_GATE2_REPO = $repoFolder; PILLION_GATE2_OUT = $json }
                    if ($p.Data) { $envVars['PILLION_GATE2_DATA'] = $p.Data }
                    $saved = @{}
                    foreach ($k in $envVars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
                    try {
                        $r = Invoke-DotnetTestRun -RepoRoot $repo -Targets @('tests\Pillion.Host.Tests\Pillion.Host.Tests.csproj') -Filter 'FullyQualifiedName~Gate2IndexingTests' -ResultsDir (Join-Path $runDir "tests-$label") -LogPath (Join-Path $runDir "test-$label.log") -NoBuild
                    }
                    finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
                    if (-not (Test-Path $json)) { throw "No result at $json; $($r.Failed -join '; ')" }
                    $o = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json -AsHashtable
                    $o['label'] = $label
                    $indexRuns.Add($o)
                    [ordered]@{
                        Status  = ($r.Totals.failed -eq 0 -and $r.Totals.passed -eq 1) ? 'Pass' : 'Fail'
                        Summary = ('{0:N0} lines, {1:N0} files, {2:N0} chunks in {3:0.0} min ({4:N0} lines/min, {5:0.0} chunks/s, {6:0.0} tokens/chunk); first searchable {7} s; {8:N0} chunks at one minute; {9:N0} bytes/chunk ({10:N0} with WAL); peak working set {11:N0} MB; {12} memory pauses; EP loaded {13}' -f $o.lines, $o.files, $o.chunks, $o.minutes, $o.linesPerMinute, $o.chunksPerSecond, $o.tokensPerChunk, $o.firstSearchableSeconds, $o.chunksAtOneMinute, $o.bytesPerChunk, $o.bytesPerChunkWithWal, $o.peakWorkingSetMB, $o.memoryPauses, $o.executionProviderLoaded)
                        result  = $o
                    }
                }))
    }
}
$data['indexRuns'] = $indexRuns.ToArray()

# e to i, with settings restored whatever happens.
$ide = $null
$restart = $null
$solution = $null
try {
    $steps.Add((Invoke-Gate0Step -Name 'e0. Back up and prepare settings.json; create the generated solution' -Body {
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
                else { $local['enabled'] = $true; $local['models'] = @(@($local['models']) + $StrongModel + $FastModel | Where-Object { $_ } | Select-Object -Unique) }
                $settings['providers'] = $providers.ToArray()
                foreach ($section in 'routing', 'features', 'privacy') { if (-not $settings.Contains($section) -or $null -eq $settings[$section]) { $settings[$section] = [ordered]@{} } }
                foreach ($section in 'tasks', 'profiles') { if (-not $settings['routing'].Contains($section) -or $null -eq $settings['routing'][$section]) { $settings['routing'][$section] = [ordered]@{} } }
                $settings['routing']['tasks']['autocomplete'] = 'localFast'
                $settings['routing']['tasks']['inlineEdit'] = 'localStrong'
                $settings['routing']['tasks']['chat'] = 'localStrong'
                $settings['routing']['profiles']['localFast'] = "$ProviderId/$FastModel"
                $settings['routing']['profiles']['localStrong'] = "$ProviderId/$StrongModel"
                foreach ($flag in 'chat', 'modelGateway', 'ollamaProvider', 'autocomplete', 'inlineChat', 'mentions', 'outboundLogView', 'localStore', 'indexing', 'embeddings', 'bundledEmbeddings', 'workspaceQueries', 'indexWindow', 'retrieval') { $settings['features'][$flag] = $true }
                $settings['privacy']['localOnly'] = $false
                Write-PillionSettings -Settings $settings
                $script:solution = New-Gate2Solution -Root $ScratchRoot
                # A fresh index for the fresh repository: the IDE scenario times the first indexing.
                $staleSolutionIndexes = @(Remove-Gate2Indexes -UnderPath $solution.Folder)
                $data['solution'] = $solution
                [ordered]@{ Summary = "backup sha256 $($b.Sha256); phase 1 and 2 flags on; generated solution $($solution.Solution): $($solution.Files) files, $($solution.Lines) lines; realistic branch: $($solution.RealisticDiff); rewrite branch: $($solution.RewriteDiff)"; backupSha256 = $b.Sha256 }
            }))

    if ($SkipIde) { Add-StatusStep 'e. IDE run' 'Skipped' 'skipped (-SkipIde)' }
    else {
        if (-not (Test-Path (Join-Path $bigRepo '.gate2-generated'))) { Write-Gate0Log "The big repository $bigRepo is missing; the big-index scenarios will fail (run without -SkipIndexing)." -Level warn }
        $steps.Add((Invoke-Gate0Step -Name "e. IDE run in the Experimental Instance (VS$($vs.Major)): integration tests, Gate 1 scenarios with retrieval on, Gate 2 scenarios" -Body {
                    $envVars = @{ PILLION_METRICS_WINDOW_SECONDS = "$MetricsWindowSec"; PILLION_GATE_PROVIDER = $ProviderId; PILLION_GATE_STRONG_MODEL = $StrongModel; PILLION_GATE_FAST_MODEL = $FastModel; PILLION_GATE2_SCENARIOS = '1'; PILLION_GATE2_SOLUTION = $solution.Solution; PILLION_GATE2_BIG_REPO = $bigRepo }
                    $r = Invoke-GateIdeRun -RepoRoot $repo -VisualStudio $vs -RunDir $runDir -ScenarioEnvironment $envVars -TestCaseFilter $TestCaseFilter -WaitForDevenvSec $WaitForDevenvSec
                    $script:ide = $r
                    $failed = @($r.Results | Where-Object Outcome -EQ 'Failed')
                    $skipped = @($r.Results | Where-Object { $_.Outcome -notin 'Passed', 'Failed' })
                    [ordered]@{
                        Status  = ($r.ExitCode -eq 0 -and $failed.Count -eq 0 -and $r.Results.Count -gt 0) ? 'Pass' : 'Fail'
                        Summary = "$(@($r.Results | Where-Object Outcome -EQ 'Passed').Count) passed, $($failed.Count) failed, $($skipped.Count) skipped; scenarios: $($r.Scenarios.Keys -join ', '); $($r.Install)"
                        trx     = $r.Trx
                        tests   = @($r.Results | ForEach-Object { "$($_.Outcome) $($_.Name) ($($_.Duration))" })
                        failed  = @($failed | ForEach-Object { "$($_.Name): $($_.Error)" })
                    }
                }))
        if ($ide) { $data['ide'] = [ordered]@{ label = $ide.Label; scenarios = $ide.Scenarios; packageLoad = $ide.PackageLoad; hostKill = $ide.HostKill; results = @($ide.Results | ForEach-Object { [ordered]@{ name = $_.Name; outcome = $_.Outcome; error = $_.Error } }) } }

        $steps.Add((Invoke-Gate0Step -Name 'e1. Visual Studio restart: the conversation of the host-kill scenario in a new session' -Body {
                    $restartDir = Join-Path $runDir 'restart'
                    $scenarioDir = Join-Path $restartDir "ide-VS$($vs.Major)\scenarios"
                    New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null
                    $earlier = Join-Path $runDir "ide-VS$($vs.Major)\scenarios\gate2-persistence-host-VS$($vs.Major).json"
                    if (-not (Test-Path $earlier)) { return [ordered]@{ Status = 'Fail'; Summary = "no result of the host-kill persistence scenario ($earlier)" } }
                    Copy-Item -LiteralPath $earlier -Destination $scenarioDir
                    $envVars = @{ PILLION_GATE2_SCENARIOS = '1'; PILLION_GATE2_RESTART_CHECK = '1'; PILLION_GATE2_SOLUTION = $solution.Solution; PILLION_GATE2_BIG_REPO = $bigRepo }
                    $r = Invoke-GateIdeRun -RepoRoot $repo -VisualStudio $vs -RunDir $restartDir -ScenarioEnvironment $envVars -TestCaseFilter 'FullyQualifiedName~Gate2_Persistence_SurvivesAVisualStudioRestart' -WaitForDevenvSec $WaitForDevenvSec
                    $script:restart = $r
                    $result = $r.Scenarios["gate2-persistence-restart-VS$($vs.Major)"]
                    $data['restart'] = $result
                    $passed = @($r.Results | Where-Object { $_.Name -like '*SurvivesAVisualStudioRestart*' -and $_.Outcome -eq 'Passed' }).Count -eq 1
                    [ordered]@{ Status = $passed ? 'Pass' : 'Fail'; Summary = "restart check $(if ($passed) { 'passed' } else { 'failed' }): $(if ($result) { $result | ConvertTo-Json -Compress } else { ($r.Results | ForEach-Object { "$($_.Outcome) $($_.Name) $($_.Error)" }) -join '; ' })" }
                }))
    }

    $steps.Add((Invoke-Gate0Step -Name 'e2. Encryption at rest: plain SQLite, key ACLs, no plain chunk text outside the database' -Body {
                $dataRoot = Join-Path $env:LOCALAPPDATA 'PillionProgrammer'
                $checks = [System.Collections.Generic.List[object]]::new()
                $acls = [System.Collections.Generic.List[object]]::new()
                $indexes = @(Get-PillionIndexFolders | Where-Object { $_.Root -and $_.Root.StartsWith($ScratchRoot, [StringComparison]::OrdinalIgnoreCase) })
                foreach ($i in $indexes | Select-Object -First 3) {
                    $db = Join-Path $i.Folder 'index.db'
                    if (Test-Path $db) { $checks.Add((Test-PlainSqliteOpen -Path $db -Scratch $ScratchRoot)) }
                    $key = Join-Path $i.Folder 'key.bin'
                    if (Test-Path $key) { $acls.Add((Get-KeyFileAcl -Path $key)) }
                }
                $conv = Join-Path $dataRoot 'conversations.db'
                if (Test-Path $conv) { $checks.Add((Test-PlainSqliteOpen -Path $conv -Scratch $ScratchRoot)) }
                $convKey = Join-Path $dataRoot 'conversations.key.bin'
                if (Test-Path $convKey) { $acls.Add((Get-KeyFileAcl -Path $convKey)) }
                # A sentence only the generated solution's code holds (a doc comment): indexed as chunk text, never written elsewhere.
                $marker = 'Handles step 5 of type 417.'
                $plain = Find-PlainText -Folders @($dataRoot) -Marker $marker
                $data['encryption'] = [ordered]@{ plainOpen = $checks.ToArray(); keyAcls = $acls.ToArray(); plainText = $plain; indexFolders = @($indexes | ForEach-Object { "$($_.Id) $($_.Root) $($_.Bytes) bytes: $($_.Files -join ', ')" }) }
                $opened = @($checks | Where-Object { $_.Opened -eq $true })
                $notChecked = @($checks | Where-Object { $null -eq $_.Opened })
                $openAcl = @($acls | Where-Object { -not $_.UserOnly })
                $status = if ($checks.Count -eq 0) { 'Fail' } elseif ($opened.Count -or $openAcl.Count -or $plain.Hits.Count) { 'Fail' } elseif ($notChecked.Count) { 'Pending' } else { 'Pass' }
                [ordered]@{ Status = $status; Summary = "plain SQLite: $(($checks | ForEach-Object { "$([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($_.Path)))\$([IO.Path]::GetFileName($_.Path)) -> $($_.Outcome)" }) -join '; '); key files user-only: $(@($acls | Where-Object UserOnly).Count) of $($acls.Count) ($(($acls | ForEach-Object { ($_.Rules -join ', ') }) | Select-Object -First 1)); '$marker' found in $($plain.Hits.Count) of $($plain.FilesSearched) files under $dataRoot$(if ($plain.Hits.Count) { ': ' + ($plain.Hits -join ', ') })" }
            }))

    if ($SkipTwoInstances -or $SkipIde) { Add-StatusStep 'e3. Two instances, one writer, stale-lock reclaim' 'Skipped' 'skipped' }
    else {
        $steps.Add((Invoke-Gate0Step -Name "e3. Two Visual Studio instances (/rootsuffix $TwoInstanceSuffix) on one index: one writer, stale lock reclaimed" -Body {
                    [void](Wait-NoDevenv -TimeoutSec $WaitForDevenvSec -Why 'the two-instance check')
                    $vsix = Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
                    $install = Install-VsixToExp -VisualStudio $vs -VsixPath $vsix -RootSuffix $TwoInstanceSuffix
                    # A small folder of its own, so both instances index quickly.
                    $folder = Join-Path $ScratchRoot 'two-instances'
                    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
                    New-Item -ItemType Directory -Force -Path $folder | Out-Null
                    for ($i = 0; $i -lt 40; $i++) { [IO.File]::WriteAllText((Join-Path $folder "File$i.cs"), (New-Gate2CodeFile -Index (900 + $i) -Lines 120)) }
                    try { $t = Test-TwoInstancesShareOneIndex -VisualStudio $vs -Folder $folder -RootSuffix $TwoInstanceSuffix }
                    finally {
                        [void](Wait-NoDevenvOfSuffix -VisualStudio $vs -RootSuffix $TwoInstanceSuffix)
                        $identity = Get-VsixManifestIdentity -VsixPath $vsix
                        $uninstall = Uninstall-VsixFromExp -VisualStudio $vs -ExtensionId $identity.Id -RootSuffix $TwoInstanceSuffix
                    }
                    $data['twoInstances'] = $t
                    [ordered]@{ Status = ($t.OneWriter -and $null -ne $t.TakeoverSeconds) ? 'Pass' : 'Fail'; Summary = "hosts $($t.Hosts -join ', '); writer $($t.Writer); taken over after $($t.TakeoverSeconds) s; install $($install.method); uninstall exit $uninstall"; timeline = $t.Timeline }
                }))
    }

    # f. Retrieval evaluation row (quoted from the baseline document).
    $steps.Add((Invoke-Gate0Step -Name 'f. Retrieval quality (docs/eval-retrieval-baseline.md, quoted)' -Body {
                $doc = Join-Path $repo 'docs\eval-retrieval-baseline.md'
                if (-not (Test-Path $doc)) { return [ordered]@{ Status = 'Pending'; Summary = 'docs/eval-retrieval-baseline.md is missing (work item 2.16)' } }
                $text = Get-Content -LiteralPath $doc -Raw
                $rows = @([regex]::Matches($text, '\| (bundled/[\w.-]+|ollama/[\w.:-]+) \| hybrid \| \*\*([\d.]+)\*\* \| \*\*([\d.]+)\*\*') | ForEach-Object { [ordered]@{ model = $_.Groups[1].Value; recall10 = [double]$_.Groups[2].Value; mrr = [double]$_.Groups[3].Value } })
                $loss = @([regex]::Matches($text, '\| pillion-src \| ([\w.-]+) \| [\d,]+ \| [\d.]+ / [\d.]+ \| ([\d.]+)( points)? \|') | ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" })
                $data['retrieval'] = [ordered]@{ hybrid = $rows; int8Loss = $loss; document = 'docs/eval-retrieval-baseline.md'; runFolders = @([regex]::Matches($text, '`(tools/eval/out/[\d-]+/)`') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique) }
                $bge = $rows | Where-Object { $_.model -like '*bge-small*' } | Select-Object -First 1
                [ordered]@{ Status = ($bge -and $bge.recall10 -ge 0.85) ? 'Pass' : 'Fail'; Summary = "hybrid recall@10 by file / MRR on pillion-src (60 queries): $(($rows | ForEach-Object { "$($_.model) $($_.recall10) / $($_.mrr)" }) -join '; '); int8 loss: $($loss -join '; ')" }
            }))

    # g. CI.
    $steps.Add((Invoke-Gate0Step -Name 'g. CI on develop' -Body {
                $ci = Get-CiRuns
                if (-not $ci) { return [ordered]@{ Status = 'Pending'; Summary = 'gh is not available or returned nothing' } }
                $data['ci'] = $ci
                $latest = $ci.runs[0]
                [ordered]@{ Status = ($latest.conclusion -eq 'success') ? 'Pass' : (($latest.status -ne 'completed') ? 'Pending' : 'Fail'); Summary = "latest develop run $($latest.url) ($($latest.sha), $($latest.status) $($latest.conclusion)); jobs: " + (($ci.latestJobs | ForEach-Object { "$($_.name) $($_.conclusion)" }) -join '; ') }
            }))

    # h. Flags.
    $steps.Add((Invoke-Gate0Step -Name 'h. Feature flags (README list, tests green)' -Body {
                $readme = Get-Content -LiteralPath (Join-Path $repo 'README.md') -Raw
                $expected = 'localStore', 'indexing', 'retrieval', 'bundledEmbeddings', 'indexWindow', 'workspaceQueries', 'reranker', 'embeddings'
                $missing = @($expected | Where-Object { $readme -notmatch "\b$_\b" })
                $unitOk = $data.Contains('unit') -and $data['unit'].totals.failed -eq 0
                [ordered]@{ Status = ($missing.Count -eq 0 -and $unitOk) ? 'Pass' : ($missing.Count ? 'Fail' : 'Pending'); Summary = "README names $($expected.Count - $missing.Count) of $($expected.Count) phase 2 flags$(if ($missing) { ' (missing: ' + ($missing -join ', ') + ')' }); unit and component tests green: $unitOk" }
            }))
}
finally {
    $steps.Add((Invoke-Gate0Step -Name 'i. Restore settings, delete the gate indexes, check processes' -Body {
                $d = [ordered]@{}
                $d['logExcerpts'] = Save-LogExcerpts -Since $runStart
                $r = Restore-PillionSettings
                $d['settingsRestored'] = $r
                $d['indexesRemoved'] = if ($KeepIndexes) { @("kept (-KeepIndexes): " + ((Get-PillionIndexFolders | Where-Object { $_.Root -and $_.Root.StartsWith($ScratchRoot, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Root }) -join '; ')) } else { @(Remove-Gate2Indexes -UnderPath $ScratchRoot) }
                # The IDE tests' scratch folders are deleted when their test ends; their indexes are left behind. Remove those whose root is gone.
                $ideScratch = Join-Path ([IO.Path]::GetTempPath()) 'pillion-ide-tests'
                $d['staleTestIndexesRemoved'] = @(Get-PillionIndexFolders | Where-Object { $_.Root -and $_.Root.StartsWith($ideScratch, [StringComparison]::OrdinalIgnoreCase) -and -not (Test-Path -LiteralPath $_.Root) } | ForEach-Object { $id = $_.Id; try { Remove-Item -LiteralPath $_.Folder -Recurse -Force; $id } catch { "$id not removed" } })
                $left = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object { $_.CommandLine -match "/rootsuffix\s+`"?$([regex]::Escape($TwoInstanceSuffix))\b" })
                foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
                $d['devenvLeft'] = @($left | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" })
                $d['hostsLeft'] = @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe'" | ForEach-Object { "$($_.ProcessId) parent $($_.ParentProcessId)" })
                try {
                    $resident = @((Invoke-RestMethod -Uri "$OllamaEndpoint/api/ps" -TimeoutSec 10).models | ForEach-Object { $_.name })
                    if ($resident -notcontains $StrongModel) {
                        $body = @{ model = $StrongModel; keep_alive = -1; options = @{ num_ctx = 4096 } } | ConvertTo-Json
                        Invoke-RestMethod -Uri "$OllamaEndpoint/api/generate" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 180 | Out-Null
                    }
                    $d['residentAtEnd'] = @((Invoke-RestMethod -Uri "$OllamaEndpoint/api/ps" -TimeoutSec 10).models | ForEach-Object { "$($_.name) (expires $($_.expires_at))" })
                }
                catch { $d['residentAtEnd'] = "could not ask Ollama: $($_.Exception.Message)" }
                $bad = ($left.Count -gt 0) -or ($r.Restored -and -not $r.MatchesBackup)
                $d['Status'] = $bad ? 'Fail' : 'Pass'
                $d['Summary'] = "settings.json restored, matches backup: $($r.MatchesBackup); gate indexes deleted: $($d['indexesRemoved'].Count); devenv left: $($left.Count); Pillion.Host left: $($d['hostsLeft'].Count); resident: $($d['residentAtEnd'] -join ', ')"
                $d
            }))
    $table = New-Gate2Table -Data $data -Steps $steps.ToArray() -VisualStudio $vs
    $run['gate2Table'] = $table.Rows
    Write-Gate0Results -Run $run -Steps $steps.ToArray() -Title "Gate 2 evidence run (VS$($vs.Major))" -FilePrefix 'gate2' -Preamble $table.Markdown
}

$failedSteps = @($steps | Where-Object status -EQ 'Fail')
$pendingSteps = @($steps | Where-Object status -EQ 'Pending')
Write-Gate0Log ("Done: {0} steps, {1} failed, {2} pending. Results in {3}" -f $steps.Count, $failedSteps.Count, $pendingSteps.Count, $runDir) -Level ($failedSteps.Count ? 'fail' : 'pass')
exit ($failedSteps.Count ? 1 : 0)
