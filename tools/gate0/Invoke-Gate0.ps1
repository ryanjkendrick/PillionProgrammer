#Requires -Version 7.5
<#
.SYNOPSIS
    Collects the Gate 0 evidence: build and deploy, tests, then a scripted run in the Visual Studio Experimental
    Instance (local chat, host kill, Local-only, cloud chat).

.DESCRIPTION
    Run from a plain PowerShell 7 prompt in the repository root:

        pwsh tools/gate0/Invoke-Gate0.ps1
        pwsh tools/gate0/Invoke-Gate0.ps1 -SkipBuild -SkipTests
        pwsh tools/gate0/Invoke-Gate0.ps1 -CloudProviderId openai      # after storing that provider's key
        pwsh tools/gate0/Invoke-Gate0.ps1 -SkipTests -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"

    Steps, each timed and logged to tools/gate0/out/<timestamp>-gate0/:
      a. Build the full solution (Release) with Visual Studio's MSBuild and deploy the VSIX to the Experimental
         Instance (with -DevEnvPath naming another version, install it with that version's VSIXInstaller instead);
         record the VSIX manifest version, `Pillion.Host.exe --version` and the Ollama version.
      b. `dotnet test PillionProgrammer.NoVsix.slnf`; TRX files and pass, fail and skip counts per project.
      c. Back up %LOCALAPPDATA%\PillionProgrammer\settings.json (restored byte for byte at the end), then write the
         run's settings: an Ollama provider (-ProviderId) and, when no cloud provider is configured, a key-less
         placeholder cloud provider so the picker has a cloud entry to grey out.
      d. Launch devenv /rootsuffix Exp on a scratch folder and open Pillion Chat (Tools > Pillion: Open Chat).
      e. Local chat: send "Reply with the single word pong" to the Ollama model; header and Outbound log line.
      f. Host kill: kill Pillion.Host three times within five minutes; UI responsiveness, Restarting then Ready,
         Failed with Open Logs after the third, then Tools > Pillion: Retry.
      g. Local-only: on through settings.json, cloud entries greyed with the reason, only local Outbound lines and no
         probes, then off again.
      h. Cloud chat: only with -CloudProviderId and a key already stored (cmdkey reports it); otherwise Pending.
      i. Close the instance, check no Pillion.Host survives, restore settings.json, write gate0-results.json and
         gate0-summary.md.

    The scripts drive only the Experimental Instance they launch, never read or store an API key, and refuse to
    start while another Experimental Instance is running.

.PARAMETER SkipBuild
    Skip step a's build and deployment (versions are still recorded from the existing build).
.PARAMETER SkipTests
    Skip step b.
.PARAMETER KeepInstanceOpen
    Leave the Experimental Instance open at the end (settings.json is still restored, so the instance then sees the
    original settings).
.PARAMETER ProviderId
    The id of the Ollama provider to use; added to settings.json for the run when it is not there. Default ollama.
.PARAMETER CloudProviderId
    The id of a configured cloud provider whose key the product owner has stored. Without it the cloud check is
    recorded as pending.
.PARAMETER Model
    The Ollama chat model. Default gemma4:e2b.
.PARAMETER ScratchRoot
    Where the scratch folder that Visual Studio opens is created. Default: %TEMP%\pillion-gate0.
.PARAMETER LocalOnlyHoldSec
    How long Local-only stays on, so that at least one probe cadence (60 s) passes without probes. Default 65.
.PARAMETER DevEnvPath
    The devenv.exe whose Experimental Instance is driven. Default: the newest Visual Studio (vswhere). The build always
    uses the newest Visual Studio's MSBuild, because the .NET 10 SDK needs MSBuild 18. When -DevEnvPath names an older
    Visual Studio (for example Visual Studio 2022 17.14), the build cannot deploy into that version's Experimental
    Instance, so step a builds with /p:DeployExtension=false and installs the VSIX with that version's
    VSIXInstaller.exe /quiet /rootSuffix:Exp (uninstalling the previous copy first). The extension stays installed after
    the run so -SkipBuild works next time; pass -UninstallAfter to remove it in step i.
.PARAMETER UninstallAfter
    With -DevEnvPath naming a Visual Studio the build does not deploy to: uninstall the extension from that
    Experimental Instance at the end (VSIXInstaller /uninstall:<VSIX id> /rootSuffix:Exp).
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$KeepInstanceOpen,
    [string]$ProviderId = 'ollama',
    [string]$CloudProviderId,
    [string]$Model = 'gemma4:e2b',
    [string]$OllamaEndpoint = 'http://localhost:11434',
    [string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) 'pillion-gate0'),
    [int]$LocalOnlyHoldSec = 65,
    [string]$DevEnvPath,
    [switch]$UninstallAfter
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'Gate0.Checks.psm1') -Force

$runDir = Initialize-Gate0Run -RepoRoot $repo -Label 'gate0'
$runStart = [DateTimeOffset]::Now
$run = [ordered]@{
    kind       = 'Invoke-Gate0'
    startedAt  = $runStart.ToString('o')
    machine    = $env:COMPUTERNAME
    parameters = [ordered]@{ SkipBuild = [bool]$SkipBuild; SkipTests = [bool]$SkipTests; KeepInstanceOpen = [bool]$KeepInstanceOpen; ProviderId = $ProviderId; CloudProviderId = $CloudProviderId; Model = $Model; DevEnvPath = $DevEnvPath; UninstallAfter = [bool]$UninstallAfter }
    versions   = [ordered]@{}
}
$steps = [System.Collections.Generic.List[object]]::new()

# Prerequisites: stop early with a clear message.
$prereq = $null
$installedExtensionId = $null
$pre = Invoke-Gate0Step -Name 'Prerequisites' -Body {
    $script:prereq = Assert-Gate0Prerequisites -Model $Model -OllamaEndpoint $OllamaEndpoint -DevEnvPath $DevEnvPath
    $v = $script:prereq.VisualStudio
    [ordered]@{ Summary = "$($v.DisplayName) $($v.Version) (build: $($v.BuildDisplayName)); Ollama $($script:prereq.Ollama.Version) with $Model" }
}
$steps.Add($pre)
if ($pre.status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray()
    Write-Error "Prerequisites not met: $($pre.error)" -ErrorAction Continue
    exit 2
}
$vs = $prereq.VisualStudio
$run.versions['Visual Studio'] = "$($vs.DisplayName) $($vs.Version) ($($vs.InstallationVersion))"
$run.versions['MSBuild'] = $vs.BuildDisplayName
$run.versions['Ollama'] = "$($prereq.Ollama.Version) at $OllamaEndpoint"
$run.versions['PowerShell'] = $PSVersionTable.PSVersion.ToString()
try { $run.versions['git commit'] = (& git -C $repo rev-parse HEAD 2>$null) + ' (' + (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null) + ')' } catch { }
try { $run.versions['.NET SDK'] = (& dotnet --version 2>$null) } catch { }

# a. Build and deploy, then versions.
$steps.Add((Invoke-Gate0Step -Name 'a. Build, deploy and versions' -Body {
            $details = [ordered]@{}
            if ($SkipBuild) { $details['build'] = 'skipped (-SkipBuild)' }
            else {
                $buildLog = Join-Path $runDir 'build.log'
                # The build deploys only into the Experimental Instance of the Visual Studio that owns the MSBuild.
                $deploy = $vs.DeployByBuild ? 'true' : 'false'
                Push-Location $repo
                try {
                    & $vs.MSBuild PillionProgrammer.sln /restore /p:Configuration=Release /m /p:DeployExtension=$deploy /nologo /v:minimal /clp:Summary 2>&1 | Out-File -LiteralPath $buildLog -Encoding utf8
                    $code = $LASTEXITCODE
                }
                finally { Pop-Location }
                $details['build'] = "MSBuild ($($vs.BuildDisplayName)) exit code $code; DeployExtension=$deploy; log $buildLog"
                $details['buildWarningsErrors'] = (Select-String -LiteralPath $buildLog -Pattern '^\s+\d+ (Warning|Error)\(s\)' | ForEach-Object { $_.Line.Trim() }) -join ', '
                if ($code -ne 0) { throw "The build failed (exit code $code); see $buildLog" }
            }
            $vsix = Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix'
            if (-not (Test-Path $vsix)) { throw "No VSIX at $vsix; build first (run without -SkipBuild)." }
            $details['vsix'] = $vsix
            $details['vsixManifestVersion'] = Get-VsixManifestVersion -VsixPath $vsix
            $built = Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.dll'
            $deployed = Find-DeployedExtension -ExpHive $vs.ExpHive
            if (-not $vs.DeployByBuild -and (-not $deployed -or -not $SkipBuild -or (Get-FileHash $built).Hash -ne (Get-FileHash $deployed.PackageDll).Hash)) {
                # Another Visual Studio's Experimental Instance: install the VSIX with its own VSIXInstaller.
                $details['install'] = Install-VsixToExp -VisualStudio $vs -VsixPath $vsix
                $script:installedExtensionId = $details['install'].extensionId
                $deployed = Find-DeployedExtension -ExpHive $vs.ExpHive
            }
            if (-not $deployed) { throw "The extension is not deployed to the Experimental Instance ($($vs.ExpHive)); run without -SkipBuild." }
            $details['deployedTo'] = $deployed.Directory
            $same = (Get-FileHash $built).Hash -eq (Get-FileHash $deployed.PackageDll).Hash
            $details['deployedMatchesBuild'] = $same
            if (-not $same) { throw 'The deployed Pillion.VisualStudio.dll differs from the build output; run without -SkipBuild so the build (or VSIXInstaller) deploys it.' }
            $details['hostVersion'] = Get-HostVersionText -HostExe $deployed.HostExe
            $details['ollamaVersion'] = (Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $Model).Version
            $run.versions['VSIX manifest'] = $details['vsixManifestVersion']
            $run.versions['Pillion.Host --version'] = $details['hostVersion']
            $details['Summary'] = "VSIX $($details['vsixManifestVersion']); $($details['hostVersion']); Ollama $($details['ollamaVersion']); deployed build matches: $same"
            $details
        }))
if ($steps[-1].status -ne 'Pass') {
    Write-Gate0Results -Run $run -Steps $steps.ToArray()
    Write-Error 'Build or deployment failed; see the run folder.' -ErrorAction Continue
    exit 1
}

# b. Tests.
if ($SkipTests) {
    $steps.Add([pscustomobject]@{ name = 'b. Tests'; status = 'Skipped'; startedAt = (Get-Date).ToString('o'); durationMs = 0; details = [ordered]@{ Summary = 'skipped (-SkipTests)' }; error = $null })
}
else {
    $steps.Add((Invoke-Gate0Step -Name 'b. Tests (dotnet test on the NoVsix filter)' -Body {
                $testDir = Join-Path $runDir 'tests'
                $testLog = Join-Path $runDir 'test.log'
                Push-Location $repo
                try {
                    & dotnet test PillionProgrammer.NoVsix.slnf -c Release --logger trx --results-directory $testDir 2>&1 | Out-File -LiteralPath $testLog -Encoding utf8
                    $code = $LASTEXITCODE
                }
                finally { Pop-Location }
                $projects = [ordered]@{}
                $trxFiles = @(Get-ChildItem -LiteralPath $testDir -Filter '*.trx' -ErrorAction SilentlyContinue)
                foreach ($trx in $trxFiles) {
                    [xml]$x = Get-Content -LiteralPath $trx.FullName -Raw
                    $ns = [System.Xml.XmlNamespaceManager]::new($x.NameTable); $ns.AddNamespace('t', $x.DocumentElement.NamespaceURI)
                    $storage = @{}
                    foreach ($ut in $x.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) { $storage[$ut.id] = [IO.Path]::GetFileNameWithoutExtension($ut.storage) }
                    foreach ($r in $x.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
                        $project = $storage[$r.testId]; if (-not $project) { $project = $trx.BaseName }
                        if (-not $projects.Contains($project)) { $projects[$project] = [ordered]@{ passed = 0; failed = 0; skipped = 0; trx = $trx.Name } }
                        switch ($r.outcome) { 'Passed' { $projects[$project].passed++ } 'Failed' { $projects[$project].failed++ } default { $projects[$project].skipped++ } }
                    }
                }
                $totals = [ordered]@{ passed = 0; failed = 0; skipped = 0 }
                foreach ($p in $projects.Values) { $totals.passed += $p.passed; $totals.failed += $p.failed; $totals.skipped += $p.skipped }
                $skippedNames = @()
                foreach ($trx in $trxFiles) {
                    [xml]$x = Get-Content -LiteralPath $trx.FullName -Raw
                    $skippedNames += @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -notin 'Passed', 'Failed' } | ForEach-Object { $_.testName })
                }
                $perProject = ($projects.Keys | ForEach-Object { "{0} {1}/{2}/{3}" -f $_, $projects[$_].passed, $projects[$_].failed, $projects[$_].skipped }) -join '; '
                [ordered]@{
                    Status     = ($code -eq 0 -and $totals.failed -eq 0 -and $trxFiles.Count -gt 0) ? 'Pass' : 'Fail'
                    Summary    = "passed $($totals.passed), failed $($totals.failed), skipped $($totals.skipped) (passed/failed/skipped: $perProject)"
                    exitCode   = $code
                    trxFiles   = @($trxFiles | ForEach-Object { $_.FullName })
                    projects   = $projects
                    totals     = $totals
                    skippedTests = $skippedNames
                    log        = $testLog
                }
            }))
}

# c to i, with settings restored and the instance closed whatever happens.
$providerInfo = $null
try {
    $steps.Add((Invoke-Gate0Step -Name 'c. Back up and prepare settings.json' -Body {
                $b = Backup-PillionSettings
                $script:providerInfo = Set-Gate0Settings -ProviderId $ProviderId -Model $Model -OllamaEndpoint $OllamaEndpoint -LocalOnly $false
                $d = [ordered]@{ Summary = "backup sha256 $($b.Sha256) (existed: $($b.Existed)); $($script:providerInfo.method)"; backupExisted = $b.Existed; backupSha256 = $b.Sha256 }
                foreach ($k in $script:providerInfo.Keys) { $d[$k] = $script:providerInfo[$k] }
                $d
            }))
    if ($steps[-1].status -eq 'Pass') {
        $folder = Join-Path $ScratchRoot ("folder-" + (Split-Path $runDir -Leaf))
        $live = Invoke-Gate0LiveChecks -VisualStudio $vs -Folder $folder -Model $Model -ProviderName $providerInfo.localProviderName -CloudProviderId $CloudProviderId -LocalOnlyHoldSec $LocalOnlyHoldSec
        foreach ($s in $live) { $steps.Add($s) }
    }
}
finally {
    $closeStep = Invoke-Gate0Step -Name 'i. Close, check for surviving hosts, restore settings' -Body {
        $d = [ordered]@{}
        if (Get-ExpPid) {
            $d['logExcerpts'] = Save-LogExcerpts -Since $runStart
            if ($KeepInstanceOpen) {
                $d['close'] = 'kept open (-KeepInstanceOpen)'
                $d['survivors'] = 'not checked (instance kept open)'
            }
            else {
                $d['close'] = Stop-ExpInstance
                $sv = Test-NoHostSurvivors
                $d['hostPidsSeen'] = $sv.hostPidsSeen
                $d['survivingHosts'] = $sv.survivors
            }
        }
        else { $d['close'] = 'no instance was started' }
        $r = Restore-PillionSettings
        $d['settingsRestored'] = $r
        if ($UninstallAfter -and $vs -and -not $vs.DeployByBuild -and -not $KeepInstanceOpen) {
            $id = if ($script:installedExtensionId) { $script:installedExtensionId } else { (Get-VsixManifestIdentity -VsixPath (Join-Path $repo 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix')).Id }
            $d['uninstallExitCode'] = Uninstall-VsixFromExp -VisualStudio $vs -ExtensionId $id
            $d['deployedAfterUninstall'] = [bool](Find-DeployedExtension -ExpHive $vs.ExpHive)
        }
        $bad = ($d['survivingHosts'] -and @($d['survivingHosts']).Count -gt 0) -or ($r.Restored -and -not $r.MatchesBackup)
        $d['Status'] = $bad ? 'Fail' : 'Pass'
        $d['Summary'] = "closed by $($d['close']); surviving hosts: $(if ($d['survivingHosts']) { $d['survivingHosts'] -join ', ' } else { 'none' }) (pids seen: $($d['hostPidsSeen'] -join ', ')); settings.json restored, matches backup: $($r.MatchesBackup)"
        $d
    }
    $steps.Add($closeStep)
    Write-Gate0Results -Run $run -Steps $steps.ToArray()
}

$failed = @($steps | Where-Object status -EQ 'Fail')
$pending = @($steps | Where-Object status -EQ 'Pending')
Write-Gate0Log ("Done: {0} steps, {1} failed, {2} pending. Results in {3}" -f $steps.Count, $failed.Count, $pending.Count, $runDir) -Level ($failed.Count ? 'fail' : 'pass')
exit ($failed.Count ? 1 : 0)
