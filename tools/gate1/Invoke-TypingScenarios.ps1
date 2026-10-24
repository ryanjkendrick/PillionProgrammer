#Requires -Version 7.5
<#
.SYNOPSIS
    Re-runs the Gate 1 typing scenarios (and the package-load tests) in the Experimental Instance of one Visual Studio,
    with retrieval on as the Gate 2 run had it, without the rest of a gate run.

.DESCRIPTION
    Work item 3.1 (issues #49 and #100) re-measures the keystroke latency with autocomplete on against off, the time to
    suggestion, the prompt-prefix reuse and the package's UI-thread time to Ready before and after its changes, on both
    Visual Studio versions. This script does what step c and step e of tools/gate2/Invoke-Gate2.ps1 do for those rows and
    nothing else: it backs up %LOCALAPPDATA%\PillionProgrammer\settings.json, writes the Ollama provider with the strong
    and fast models and every phase 1 and phase 2 flag on (retrieval included), installs this build into the Experimental
    Instance when the deployed copy differs, runs the integration tests that match -TestCaseFilter with
    PILLION_GATE_SCENARIOS=1 and PILLION_UI_THREAD_TIMINGS=1 (through tools/vs-tests/Invoke-IdeTests.ps1), and restores
    settings.json byte for byte. The scenario JSON files, the TRX file and the logs land in
    tools/gate1/out/<timestamp>-<Label>/ide-VS<major>/ (git-ignored); a short summary of the figures is printed and
    written to summary.md there.

    The default filter runs the three typing passes (strong model, fast model, autocomplete off) and the two package-load
    tests, which must run together (the second reads what the first measured). Pass -TestCaseFilter to run more.

    Run from a plain PowerShell 7 prompt with Ollama running and the models pulled, with no Experimental Instance open:

        pwsh tools/gate1/Invoke-TypingScenarios.ps1 -Label before
        pwsh tools/gate1/Invoke-TypingScenarios.ps1 -Label after -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"

.PARAMETER DevEnvPath
    The devenv.exe whose Experimental Instance is driven. Default: the newest Visual Studio.
.PARAMETER Label
    A word for the run folder's name (for example before, after).
.PARAMETER TestCaseFilter
    The vstest filter (combined with the version filter by Invoke-IdeTests.ps1).
#>
[CmdletBinding()]
param(
    [string]$DevEnvPath,
    [string]$Label = 'typing',
    [string]$TestCaseFilter = 'FullyQualifiedName~Package_LoadsOnFolderOpen|FullyQualifiedName~PackageLoad_UsesLittleUiThreadCpu|FullyQualifiedName~Gate1_Autocomplete|FullyQualifiedName~Gate1_Keystrokes',
    [string]$ProviderId = 'ollama',
    [string]$StrongModel = 'qwen2.5-coder:7b',
    [string]$FastModel = 'qwen2.5-coder:1.5b',
    [string]$OllamaEndpoint = 'http://localhost:11434',
    [int]$MetricsWindowSec = 30,
    [int]$WaitForDevenvSec = 3600
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repo 'tools\gate0\Gate0.Checks.psm1') -Force
Import-Module (Join-Path $repo 'tools\gate1\Gate1.Scenarios.psm1') -Force

$runDir = Initialize-Gate0Run -RepoRoot $repo -Label $Label -OutRoot 'tools/gate1/out'
$pre = Assert-Gate0Prerequisites -Model $StrongModel -OllamaEndpoint $OllamaEndpoint -DevEnvPath $DevEnvPath
$fast = Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $FastModel
if (-not $fast.ModelPulled) { throw "The model '$FastModel' is not pulled in Ollama." }
$vs = $pre.VisualStudio
Write-Gate0Log "Driving $($vs.DisplayName) $($vs.Version); Ollama $($pre.Ollama.Version); commit $(& git -C $repo rev-parse --short HEAD) ($(& git -C $repo rev-parse --abbrev-ref HEAD))"

$backup = Backup-PillionSettings
$result = $null
try {
    # Step c of Invoke-Gate2.ps1: the Ollama provider as Local-strong and Local-fast, every phase 1 and phase 2 flag on.
    $settings = Read-PillionSettings
    if ($null -eq $settings) { $settings = New-DefaultPillionSettings }
    $providers = [System.Collections.Generic.List[object]]::new()
    foreach ($p in @($settings['providers'])) { if ($null -ne $p) { $providers.Add($p) } }
    $local = $providers | Where-Object { $_['id'] -eq $ProviderId } | Select-Object -First 1
    if (-not $local) {
        $local = [ordered]@{ id = $ProviderId; kind = 'ollama'; displayName = 'Ollama'; endpoint = $OllamaEndpoint; models = @($StrongModel, $FastModel); enabled = $true }
        $providers.Insert(0, $local)
    }
    elseif ($local['kind'] -ne 'ollama') { throw "Provider '$ProviderId' in settings.json has kind '$($local['kind'])', not ollama." }
    else {
        $local['enabled'] = $true
        $models = [System.Collections.Generic.List[object]]::new()
        foreach ($m in @($local['models'])) { if ($null -ne $m) { $models.Add($m) } }
        foreach ($m in $StrongModel, $FastModel) { if ($models -notcontains $m) { $models.Add($m) } }
        $local['models'] = $models.ToArray()
    }
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
    Write-Gate0Log 'settings.json written for the run (restored at the end).'

    $envVars = @{ PILLION_METRICS_WINDOW_SECONDS = "$MetricsWindowSec"; PILLION_GATE_PROVIDER = $ProviderId; PILLION_GATE_STRONG_MODEL = $StrongModel; PILLION_GATE_FAST_MODEL = $FastModel }
    $result = Invoke-GateIdeRun -RepoRoot $repo -VisualStudio $vs -RunDir $runDir -ScenarioEnvironment $envVars -TestCaseFilter $TestCaseFilter -WaitForDevenvSec $WaitForDevenvSec
}
finally {
    $restored = Restore-PillionSettings
    Write-Gate0Log "settings.json restored: $($restored | ConvertTo-Json -Compress)"
}

# The summary: the rows work item 3.1 reports.
function Get-Path($object, [string]$path) {
    $current = $object
    foreach ($part in $path -split '\.') {
        if ($null -eq $current) { return $null }
        if ($current -is [System.Collections.IDictionary]) { $current = $current[$part] } else { $current = $current.$part }
    }
    return $current
}
function Fmt($value, [string]$format = '0.00') { if ($null -eq $value) { 'n/a' } else { ([double]$value).ToString($format, [cultureinfo]::InvariantCulture) } }

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Typing scenarios: $Label on $($vs.DisplayName) $($vs.Version)")
$lines.Add('')
$lines.Add("Run folder: $runDir; commit $(& git -C $repo rev-parse --short HEAD); tests: $(@($result.Results | Where-Object Outcome -EQ 'Passed').Count) passed, $(@($result.Results | Where-Object Outcome -EQ 'Failed').Count) failed, $(@($result.Results | Where-Object { $_.Outcome -notin 'Passed', 'Failed' }).Count) skipped; install: $($result.Install); waited for devenv $($result.WaitedForDevenvSec) s.")
$lines.Add('')
foreach ($r in @($result.Results | Where-Object Outcome -EQ 'Failed')) { $lines.Add("- FAILED $($r.Name): $($r.Error)") }
$major = $vs.Major
$strong = $result.Scenarios["autocomplete-strong-VS$major"]
$fastRun = $result.Scenarios["autocomplete-fast-VS$major"]
$off = $result.Scenarios["autocomplete-off-VS$major"]
$lines.Add('| Row | Figure |')
$lines.Add('| --- | --- |')
foreach ($pass in @(@{ Name = 'strong (7B)'; Data = $strong }, @{ Name = 'fast (1.5B)'; Data = $fastRun }, @{ Name = 'off'; Data = $off })) {
    $d = $pass.Data
    if ($null -eq $d) { $lines.Add("| Keystrokes, autocomplete $($pass.Name) | not measured |"); continue }
    $lines.Add("| Keystrokes, autocomplete $($pass.Name) | TypeChar p50 $(Fmt (Get-Path $d 'typing.typeChar.p50')) ms, p95 $(Fmt (Get-Path $d 'typing.typeChar.p95')) ms, max $(Fmt (Get-Path $d 'typing.typeChar.max')) ms over $(Get-Path $d 'typing.typedCharacters') keystrokes; with a Pillion proposal displayed before the keystroke p95 $(Fmt (Get-Path $d 'typing.typeCharByProposalDisplayedBefore.pillion.p95')) ms ($(Get-Path $d 'typing.typeCharByProposalDisplayedBefore.pillion.count')), another provider's $(Fmt (Get-Path $d 'typing.typeCharByProposalDisplayedBefore.other.p95')) ms ($(Get-Path $d 'typing.typeCharByProposalDisplayedBefore.other.count')), none $(Fmt (Get-Path $d 'typing.typeCharByProposalDisplayedBefore.none.p95')) ms ($(Get-Path $d 'typing.typeCharByProposalDisplayedBefore.none.count')); Pillion's own UI-thread time inside TypeChar $(Fmt (Get-Path $d 'typing.pillionUiThread.msInTypeChar')) ms in all, per keystroke p95 $(Fmt (Get-Path $d 'typing.pillionUiThread.pillionMsPerTypeChar.p95') '0.000') ms, max $(Fmt (Get-Path $d 'typing.pillionUiThread.pillionMsPerTypeChar.max') '0.000') ms; accepted $(Get-Path $d 'typing.acceptedProposals') proposals |")
    if ($pass.Name -ne 'off') {
        $lines.Add("| Time to suggestion, $($pass.Name) | p50 $(Fmt (Get-Path $d 'autocomplete.timeToSuggestion.p50') '0') ms, p95 $(Fmt (Get-Path $d 'autocomplete.timeToSuggestion.p95') '0') ms; first chunk p50 $(Fmt (Get-Path $d 'autocomplete.timeToFirstChunk.p50') '0') ms; requests $(Get-Path $d 'autocomplete.requests'), shown $(Get-Path $d 'autocomplete.shown'), accepted $(Get-Path $d 'autocomplete.accepted'); prose $(Get-Path $d 'typing.proseProposals'), fenced $(Get-Path $d 'typing.fencedProposals') |")
        $lines.Add("| Prompt-prefix reuse, $($pass.Name) | $(Get-Path $d 'autocomplete.host.promptPrefixReuseRate') ($(Get-Path $d 'autocomplete.host.promptPrefixReuses') of $(Get-Path $d 'autocomplete.host.promptPrefixChecks')); host cache hit rate $(Get-Path $d 'autocomplete.host.hitRate') |")
    }
}
if ($strong -and $off) {
    $lines.Add("| Keystrokes on (7B) against off | p50 $(Fmt ((Get-Path $strong 'typing.typeChar.p50') - (Get-Path $off 'typing.typeChar.p50')) '+0.00;-0.00') ms, p95 $(Fmt ((Get-Path $strong 'typing.typeChar.p95') - (Get-Path $off 'typing.typeChar.p95')) '+0.00;-0.00') ms |")
}
if ($fastRun -and $off) {
    $lines.Add("| Keystrokes on (1.5B) against off | p50 $(Fmt ((Get-Path $fastRun 'typing.typeChar.p50') - (Get-Path $off 'typing.typeChar.p50')) '+0.00;-0.00') ms, p95 $(Fmt ((Get-Path $fastRun 'typing.typeChar.p95') - (Get-Path $off 'typing.typeChar.p95')) '+0.00;-0.00') ms |")
}
$loadLines = @(Select-OutputLines -Results $result.Results -TestPattern 'PackageLoad_UsesLittleUiThreadCpu' -LinePattern '^(PackageLoadTimings|PackageDeferredTimings|Pillion''s work deferred|UI thread CPU from)' | ForEach-Object Line)
foreach ($l in $loadLines) { $lines.Add("| Package load | $l |") }
foreach ($pass in @(@{ Name = 'strong (7B)'; Data = $strong }, @{ Name = 'fast (1.5B)'; Data = $fastRun }, @{ Name = 'off'; Data = $off })) {
    $d = $pass.Data
    if ($null -eq $d) { continue }
    $lines.Add('')
    $lines.Add("Slowest keystrokes, autocomplete $($pass.Name): " + ((@(Get-Path $d 'typing.typeCharSlowest') | ForEach-Object { "$(Fmt $_.ms) ms (line $($_.line) '$($_.char)', $($_.proposalDisplayedBefore) displayed, Pillion $(Fmt $_.pillionMs '0.000') ms$(if ($_.stretches.Count -gt 0) { ': ' + (($_.stretches | ForEach-Object { "$($_.name) $(Fmt $_.ms '0.000')" }) -join ', ') }))" }) -join '; '))
    $lines.Add("Pillion UI-thread stretches in the pass, autocomplete $($pass.Name): " + ((@((Get-Path $d 'typing.pillionUiThread.stretchesInPass').GetEnumerator()) | ForEach-Object { "$($_.Key) x$($_.Value.count) $(Fmt $_.Value.ms) ms" }) -join ', '))
}
$summary = $lines -join [Environment]::NewLine
$summary | Set-Content -LiteralPath (Join-Path $runDir 'summary.md') -Encoding utf8
Write-Host $summary
exit $result.ExitCode
