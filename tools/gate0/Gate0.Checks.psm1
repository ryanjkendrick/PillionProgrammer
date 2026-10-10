#Requires -Version 7.5
<#
.SYNOPSIS
    Shared helpers and checks for the Gate 0 evidence scripts (Invoke-Gate0.ps1 and Invoke-OfflineRun.ps1).

.DESCRIPTION
    Everything here acts only on the Visual Studio Experimental Instance that Start-ExpInstance launched: UI
    Automation queries are rooted at that process's main window, and process queries filter on its process id. No
    other window or process is touched.

    Settings: the scripts back up %LOCALAPPDATA%\PillionProgrammer\settings.json byte for byte and restore it in a
    finally block. settings.json never holds an API key (keys live in the Windows Credential Manager); these scripts
    never read, store or log a key. The cloud check only asks `cmdkey /list:<target>` whether a credential exists.
#>

Set-StrictMode -Version 3.0

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

if (-not ('Gate0Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class Gate0Native
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Returns the object registered in the Running Object Table under an exact display name, or null.</summary>
    public static object GetRunningObject(string displayName)
    {
        IRunningObjectTable rot;
        if (GetRunningObjectTable(0, out rot) != 0) return null;
        IEnumMoniker monikers;
        rot.EnumRunning(out monikers);
        var one = new IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            IBindCtx ctx;
            CreateBindCtx(0, out ctx);
            string name;
            one[0].GetDisplayName(ctx, null, out name);
            if (string.Equals(name, displayName, StringComparison.Ordinal))
            {
                object o;
                rot.GetObject(one[0], out o);
                return o;
            }
        }
        return null;
    }

    /// <summary>Sends WM_NULL with SMTO_ABORTIFHUNG; true when the window's thread answered within the timeout.</summary>
    public static bool Ping(IntPtr hWnd, uint timeoutMs)
    {
        IntPtr result;
        return SendMessageTimeout(hWnd, 0x0000, IntPtr.Zero, IntPtr.Zero, 0x0002, timeoutMs, out result) != IntPtr.Zero;
    }
}
'@
}

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TreeScope = [System.Windows.Automation.TreeScope]
$script:CT = [System.Windows.Automation.ControlType]

# Run context, filled by Initialize-Gate0Run and Start-ExpInstance.
$script:Gate0 = [ordered]@{
    RunDir      = $null
    LogFile     = $null
    RepoRoot    = $null
    ExpPid      = 0
    ExpProcess  = $null
    MainHwnd    = [IntPtr]::Zero
    MainElement = $null
    ChatElement = $null
    HostPids    = [System.Collections.Generic.HashSet[int]]::new()
    VsMajor     = $null
    LaunchedAt  = $null
    SettingsBackup = $null
}

$script:PillionDataDir = Join-Path $env:LOCALAPPDATA 'PillionProgrammer'
$script:SettingsPath = Join-Path $script:PillionDataDir 'settings.json'
$script:LogDir = Join-Path $script:PillionDataDir 'logs'

#region Logging, steps and results

function Initialize-Gate0Run {
    <# Creates tools/gate0/out/<timestamp>/ (or the given folder) and starts the run log. Returns the folder. #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$Label = 'gate0'
    )
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dir = Join-Path $RepoRoot "tools/gate0/out/$stamp-$Label"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:Gate0.RunDir = (Resolve-Path $dir).Path
    $script:Gate0.LogFile = Join-Path $script:Gate0.RunDir 'run.log'
    $script:Gate0.RepoRoot = (Resolve-Path $RepoRoot).Path
    Write-Gate0Log "Run folder: $($script:Gate0.RunDir)"
    return $script:Gate0.RunDir
}

function Get-Gate0RunDir { $script:Gate0.RunDir }

function Write-Gate0Log {
    param([Parameter(Mandatory, Position = 0)][string]$Message, [ValidateSet('info', 'warn', 'error', 'pass', 'fail')][string]$Level = 'info')
    $line = '{0} {1,-5} {2}' -f (Get-Date -Format 'HH:mm:ss.fff'), $Level, $Message
    $color = switch ($Level) { 'warn' { 'Yellow' } 'error' { 'Red' } 'fail' { 'Red' } 'pass' { 'Green' } default { 'Gray' } }
    Write-Host $line -ForegroundColor $color
    if ($script:Gate0.LogFile) { Add-Content -LiteralPath $script:Gate0.LogFile -Value $line -Encoding utf8 }
}

function Invoke-Gate0Step {
    <#
    Runs one timed step. The body returns a hashtable of details; its 'Status' key (Pass, Fail, Pending, Skipped)
    becomes the step status (default Pass). An exception makes the step Fail with the message recorded.
    #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    Write-Gate0Log "=== $Name"
    $started = Get-Date
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $result = [ordered]@{ name = $Name; status = 'Pass'; startedAt = $started.ToString('o'); durationMs = 0; details = [ordered]@{}; error = $null }
    try {
        $details = & $Body
        if ($details -is [System.Collections.IDictionary]) {
            foreach ($k in $details.Keys) {
                if ($k -eq 'Status') { $result.status = [string]$details[$k] } else { $result.details[$k] = $details[$k] }
            }
        }
    }
    catch {
        $result.status = 'Fail'
        $result.error = $_.Exception.Message
        Write-Gate0Log "$Name failed: $($_.Exception.Message)" -Level error
        Write-Gate0Log ($_.ScriptStackTrace -replace "`r?`n", ' | ') -Level error
    }
    $sw.Stop()
    $result.durationMs = [int]$sw.ElapsedMilliseconds
    $level = switch ($result.status) { 'Pass' { 'pass' } 'Fail' { 'fail' } default { 'warn' } }
    Write-Gate0Log ("{0}: {1} in {2:n1} s" -f $Name, $result.status, ($result.durationMs / 1000)) -Level $level
    return [pscustomobject]$result
}

function Write-Gate0Results {
    <# Writes gate0-results.json and gate0-summary.md into the run folder. #>
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Run,
        [Parameter(Mandatory)][object[]]$Steps,
        [string]$Title = 'Gate 0 evidence run'
    )
    $Run['steps'] = $Steps
    $failed = @($Steps | Where-Object status -EQ 'Fail')
    $Run['overall'] = if ($failed.Count -gt 0) { 'Fail' } else { 'Pass' }
    $Run['finishedAt'] = (Get-Date).ToString('o')
    $jsonPath = Join-Path $script:Gate0.RunDir 'gate0-results.json'
    $Run | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8

    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("# $Title")
    [void]$md.AppendLine()
    [void]$md.AppendLine("Run folder: ``$($script:Gate0.RunDir)``  ")
    [void]$md.AppendLine("Started $($Run['startedAt']), finished $($Run['finishedAt']). Overall: **$($Run['overall'])** (Pending items are not failures).")
    [void]$md.AppendLine()
    if ($Run.Contains('versions')) {
        [void]$md.AppendLine('## Versions')
        [void]$md.AppendLine()
        foreach ($k in $Run['versions'].Keys) { [void]$md.AppendLine("- ${k}: $($Run['versions'][$k])") }
        [void]$md.AppendLine()
    }
    [void]$md.AppendLine('## Steps')
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Step | Status | Duration | Summary |')
    [void]$md.AppendLine('| --- | --- | --- | --- |')
    foreach ($s in $Steps) {
        $summary = if ($s.details.Contains('Summary')) { $s.details['Summary'] } elseif ($s.error) { "Error: $($s.error)" } else { '' }
        $summary = ($summary -replace '\|', '\|' -replace "`r?`n", ' ')
        [void]$md.AppendLine(("| {0} | {1} | {2:n1} s | {3} |" -f $s.name, $s.status, ($s.durationMs / 1000), $summary))
    }
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Details')
    foreach ($s in $Steps) {
        [void]$md.AppendLine()
        [void]$md.AppendLine("### $($s.name): $($s.status)")
        [void]$md.AppendLine()
        if ($s.error) { [void]$md.AppendLine("Error: $($s.error)"); [void]$md.AppendLine() }
        [void]$md.AppendLine('```json')
        [void]$md.AppendLine(($s.details | ConvertTo-Json -Depth 8))
        [void]$md.AppendLine('```')
    }
    $mdPath = Join-Path $script:Gate0.RunDir 'gate0-summary.md'
    Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding utf8
    Write-Gate0Log "Results: $jsonPath"
    Write-Gate0Log "Summary: $mdPath"
}

#endregion

#region Prerequisites and versions

function Find-VisualStudio {
    <#
    Finds the Visual Studio to build with and the one to drive, through vswhere. Throws a clear message when there is none.

    The build always uses the newest Visual Studio's MSBuild (the .NET 10 SDK needs MSBuild 18). The instance that is
    launched and driven is the newest one too, unless -DevEnvPath names another devenv.exe (for example Visual Studio
    2022's). When the two differ, DeployByBuild is false: the build cannot deploy into the other version's
    Experimental Instance, so the caller installs the VSIX with the driven version's VSIXInstaller instead.
    #>
    param([string]$DevEnvPath)
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "Visual Studio not found: vswhere.exe is missing ($vswhere). Install Visual Studio 2026 with the extension development workload." }
    # Pick the newest Visual Studio by version (not by install date): with Visual Studio 2022 installed beside 2026,
    # the run must still build with MSBuild 18.
    $json = & $vswhere -products * -requires Microsoft.Component.MSBuild -format json | ConvertFrom-Json
    $build = @($json) | Where-Object { $_ } | Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
    if (-not $build) { throw 'Visual Studio not found: vswhere reports no instance with MSBuild. Install Visual Studio 2026 with the extension development workload.' }
    $msbuild = Join-Path $build.installationPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path $msbuild)) { throw "MSBuild.exe not found at $msbuild." }

    $vs = $build
    if ($DevEnvPath) {
        if (-not (Test-Path -LiteralPath $DevEnvPath -PathType Leaf)) { throw "-DevEnvPath: no file at '$DevEnvPath'." }
        $full = (Resolve-Path -LiteralPath $DevEnvPath).Path
        $all = & $vswhere -all -products * -format json | ConvertFrom-Json
        $vs = @($all) | Where-Object { $_ -and $full.StartsWith((Join-Path $_.installationPath 'Common7\IDE\'), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $vs) { throw "-DevEnvPath: '$full' does not belong to any Visual Studio instance vswhere knows." }
    }
    $devenv = Join-Path $vs.installationPath 'Common7\IDE\devenv.exe'
    if (-not (Test-Path $devenv)) { throw "Visual Studio found at $($vs.installationPath) but devenv.exe is missing." }
    $major = ($vs.installationVersion -split '\.')[0]
    $script:Gate0.VsMajor = $major
    [pscustomobject]@{
        DisplayName = $vs.displayName
        Version     = $vs.catalog.productDisplayVersion
        InstallationVersion = $vs.installationVersion
        InstanceId  = $vs.instanceId
        Major       = $major
        Path        = $vs.installationPath
        DevEnv      = $devenv
        VsixInstaller = Join-Path $vs.installationPath 'Common7\IDE\VSIXInstaller.exe'
        MSBuild     = $msbuild
        BuildDisplayName = "$($build.displayName) $($build.catalog.productDisplayVersion)"
        BuildMajor  = ($build.installationVersion -split '\.')[0]
        DeployByBuild = $vs.instanceId -eq $build.instanceId
        ExpHive     = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$major.0_$($vs.instanceId)Exp"
    }
}

function Get-OllamaStatus {
    <# Returns the Ollama version and whether the model is pulled. Throws a clear message when Ollama is not running. #>
    param([string]$Endpoint = 'http://localhost:11434', [Parameter(Mandatory)][string]$Model)
    try { $version = (Invoke-RestMethod -Uri "$Endpoint/api/version" -TimeoutSec 5).version }
    catch { throw "Ollama is not running at $Endpoint ($($_.Exception.Message)). Start Ollama and try again." }
    $tags = Invoke-RestMethod -Uri "$Endpoint/api/tags" -TimeoutSec 10
    $names = @($tags.models | ForEach-Object { $_.name })
    $pulled = $names -contains $Model -or $names -contains "${Model}:latest"
    [pscustomobject]@{ Endpoint = $Endpoint; Version = $version; Model = $Model; ModelPulled = $pulled; Models = $names }
}

function Assert-Gate0Prerequisites {
    <# Checks PowerShell, Windows, Visual Studio, Ollama and the model, and that no Experimental Instance is running. #>
    param([Parameter(Mandatory)][string]$Model, [string]$OllamaEndpoint = 'http://localhost:11434', [string]$DevEnvPath)
    if ($PSVersionTable.PSVersion.Major -lt 7 -or ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 5)) { throw 'PowerShell 7.5 or later is required (run pwsh).' }
    if (-not $IsWindows) { throw 'These scripts run on Windows only.' }
    $vs = Find-VisualStudio -DevEnvPath $DevEnvPath
    if ([int]$vs.BuildMajor -lt 18) { Write-Gate0Log "Building with $($vs.BuildDisplayName); the full solution needs MSBuild 18 (Visual Studio 2026)." -Level warn }
    if (-not $vs.DeployByBuild) {
        if (-not (Test-Path $vs.VsixInstaller)) { throw "VSIXInstaller.exe not found at $($vs.VsixInstaller); it installs the VSIX into the $($vs.DisplayName) Experimental Instance." }
        Write-Gate0Log "Driving $($vs.DisplayName) $($vs.Version) ($($vs.DevEnv)); building with $($vs.BuildDisplayName); the VSIX is installed with $($vs.VsixInstaller) /rootSuffix:Exp."
    }
    $ollama = Get-OllamaStatus -Endpoint $OllamaEndpoint -Model $Model
    if (-not $ollama.ModelPulled) { throw "The model '$Model' is not pulled in Ollama (installed: $($ollama.Models -join ', ')). Run: ollama pull $Model" }
    $exp = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object { $_.CommandLine -match '/rootsuffix\s+Exp\b' })
    if ($exp.Count -gt 0) { throw "An Experimental Instance is already running (pid $($exp.ProcessId -join ', ')). Close it first; these scripts only drive an instance they launch." }
    $others = @(Get-Process -Name 'Pillion.Host' -ErrorAction SilentlyContinue)
    if ($others.Count -gt 0) { Write-Gate0Log "Other Pillion.Host processes are running (pid $($others.Id -join ', ')); their lines may appear in the shared host log. They are not touched." -Level warn }
    [pscustomobject]@{ VisualStudio = $vs; Ollama = $ollama }
}

function Get-VsixManifestIdentity {
    <# The Identity element (Id, Version) of a .vsix's extension.vsixmanifest. #>
    param([Parameter(Mandatory)][string]$VsixPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($VsixPath)
    try {
        $entry = $zip.GetEntry('extension.vsixmanifest')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $xml.PackageManifest.Metadata.Identity
        return [pscustomobject]@{ Id = $identity.Id; Version = $identity.Version }
    }
    finally { $zip.Dispose() }
}

function Get-VsixManifestVersion {
    param([Parameter(Mandatory)][string]$VsixPath)
    return (Get-VsixManifestIdentity -VsixPath $VsixPath).Version
}

function Install-VsixToExp {
    <#
    Installs a .vsix into the Experimental Instance of the driven Visual Studio with that version's VSIXInstaller
    (/quiet /rootSuffix:Exp), uninstalling an earlier copy of the same extension first so the new build replaces it.
    Used when the build's own deployment cannot reach that instance (for example building with Visual Studio 2026's
    MSBuild and driving Visual Studio 2022). Returns what was done.
    #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$VsixPath)
    $identity = Get-VsixManifestIdentity -VsixPath $VsixPath
    $un =Start-Process -FilePath $VisualStudio.VsixInstaller -ArgumentList @('/quiet', '/rootSuffix:Exp', "/uninstall:$($identity.Id)") -PassThru -Wait
    $in = Start-Process -FilePath $VisualStudio.VsixInstaller -ArgumentList @('/quiet', '/rootSuffix:Exp', "`"$VsixPath`"") -PassThru -Wait
    Write-Gate0Log ("VSIXInstaller: uninstall {0} exit {1}; install exit {2} ({3})" -f $identity.Id, $un.ExitCode, $in.ExitCode, $VisualStudio.VsixInstaller)
    if ($in.ExitCode -ne 0) { throw "VSIXInstaller failed to install $VsixPath into $($VisualStudio.ExpHive) (exit code $($in.ExitCode)); see the newest %TEMP%\dd_VSIXInstaller_*.log." }
    # On Visual Studio 2022 17.14 the reinstall did not mark the Experimental Instance's configuration as changed, so
    # devenv kept its cached pkgdef configuration and never loaded the package (no Pillion commands on the Tools menu).
    # Rebuilding the configuration explicitly fixes that.
    $cfg = Start-Process -FilePath $VisualStudio.DevEnv -ArgumentList @('/rootsuffix', 'Exp', '/updateconfiguration') -PassThru -Wait
    Write-Gate0Log "devenv /rootsuffix Exp /updateconfiguration exit $($cfg.ExitCode)"
    [ordered]@{ method = "$($VisualStudio.VsixInstaller) /quiet /rootSuffix:Exp, then devenv /rootsuffix Exp /updateconfiguration"; extensionId = $identity.Id; uninstallExitCode = $un.ExitCode; installExitCode = $in.ExitCode; updateConfigurationExitCode = $cfg.ExitCode }
}

function Uninstall-VsixFromExp {
    <# Removes the extension (by its VSIX identity) from the driven Visual Studio's Experimental Instance. #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$ExtensionId)
    $un = Start-Process -FilePath $VisualStudio.VsixInstaller -ArgumentList @('/quiet', '/rootSuffix:Exp', "/uninstall:$ExtensionId") -PassThru -Wait
    Write-Gate0Log "VSIXInstaller: uninstall $ExtensionId from $($VisualStudio.ExpHive) exit $($un.ExitCode)"
    # Drop the package from the cached configuration as well (see Install-VsixToExp).
    $cfg = Start-Process -FilePath $VisualStudio.DevEnv -ArgumentList @('/rootsuffix', 'Exp', '/updateconfiguration') -PassThru -Wait
    Write-Gate0Log "devenv /rootsuffix Exp /updateconfiguration exit $($cfg.ExitCode)"
    return $un.ExitCode
}

function Find-DeployedExtension {
    <# Finds the Pillion extension folder in the Experimental Instance hive. #>
    param([Parameter(Mandatory)][string]$ExpHive)
    $ext = Join-Path $ExpHive 'Extensions'
    if (-not (Test-Path $ext)) { return $null }
    $exe = Get-ChildItem -Path $ext -Recurse -Filter 'Pillion.Host.exe' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { return $null }
    $dir = Split-Path (Split-Path $exe.FullName -Parent) -Parent
    [pscustomobject]@{ Directory = $dir; HostExe = $exe.FullName; PackageDll = Join-Path $dir 'Pillion.VisualStudio.dll' }
}

function Get-HostVersionText {
    param([Parameter(Mandatory)][string]$HostExe)
    $text = & $HostExe --version 2>&1 | Out-String
    return $text.Trim()
}

#endregion

#region Settings

function Backup-PillionSettings {
    <# Keeps the exact bytes of settings.json (or notes that it is absent) in memory and in the run folder. #>
    $backup = [ordered]@{ Existed = (Test-Path -LiteralPath $script:SettingsPath); Bytes = $null; Sha256 = $null }
    if ($backup.Existed) {
        $backup.Bytes = [IO.File]::ReadAllBytes($script:SettingsPath)
        $backup.Sha256 = (Get-FileHash -LiteralPath $script:SettingsPath -Algorithm SHA256).Hash
        if ($script:Gate0.RunDir) { [IO.File]::WriteAllBytes((Join-Path $script:Gate0.RunDir 'settings.backup.json'), $backup.Bytes) }
    }
    $script:Gate0.SettingsBackup = $backup
    Write-Gate0Log ("settings.json backed up (existed: {0}, sha256 {1})" -f $backup.Existed, $backup.Sha256)
    return $backup
}

function Restore-PillionSettings {
    <# Restores settings.json byte for byte (or deletes it when it did not exist). Returns the verification. #>
    $backup = $script:Gate0.SettingsBackup
    if ($null -eq $backup) { return [pscustomobject]@{ Restored = $false; Reason = 'no backup was taken' } }
    if ($backup.Existed) {
        [IO.File]::WriteAllBytes($script:SettingsPath, $backup.Bytes)
        $hash = (Get-FileHash -LiteralPath $script:SettingsPath -Algorithm SHA256).Hash
        $ok = $hash -eq $backup.Sha256
        Write-Gate0Log ("settings.json restored (sha256 {0}, matches backup: {1})" -f $hash, $ok) -Level ($ok ? 'info' : 'error')
        return [pscustomobject]@{ Restored = $true; Sha256 = $hash; MatchesBackup = $ok }
    }
    if (Test-Path -LiteralPath $script:SettingsPath) { Remove-Item -LiteralPath $script:SettingsPath -Force }
    Write-Gate0Log 'settings.json did not exist before the run; removed the one the run created.'
    return [pscustomobject]@{ Restored = $true; Sha256 = $null; MatchesBackup = -not (Test-Path -LiteralPath $script:SettingsPath) }
}

function Read-PillionSettings {
    if (-not (Test-Path -LiteralPath $script:SettingsPath)) { return $null }
    return Get-Content -LiteralPath $script:SettingsPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
}

function Write-PillionSettings {
    <# Writes settings.json atomically (temporary file, then replace), so Pillion Host never reads half a file. #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Settings)
    New-Item -ItemType Directory -Force -Path $script:PillionDataDir | Out-Null
    $tmp = "$script:SettingsPath.gate0.tmp"
    $json = $Settings | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($tmp, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $script:SettingsPath -Force
}

function New-DefaultPillionSettings {
    [ordered]@{
        version       = 1
        providers     = @()
        routing       = [ordered]@{
            tasks    = [ordered]@{ autocomplete = 'auto'; inlineEdit = 'auto'; chat = 'auto'; agent = 'auto'; embedding = 'auto'; vision = 'auto' }
            profiles = [ordered]@{ fast = ''; balanced = ''; frontier = ''; localFast = ''; localStrong = ''; auto = '' }
        }
        cloudSwitches = [ordered]@{ chat = $true; inlineEdit = $true; autocomplete = $false; agent = $false; selfHealing = $false; vision = $true }
        privacy       = [ordered]@{ localOnly = $false; telemetryOptIn = $false; redactForLocalProviders = $false }
        approvals     = [ordered]@{ mode = 'ask' }
        costCaps      = [ordered]@{}
        indexing      = [ordered]@{ exclude = @(); maxMemoryMb = 1536; pauseOnBattery = $true }
        logging       = [ordered]@{ level = 'information'; rpcTrace = $false }
    }
}

function Test-IsLocalEndpoint {
    param([string]$Endpoint)
    try { $uriHost = ([Uri]$Endpoint).Host } catch { return $false }
    return $uriHost -eq 'localhost' -or $uriHost -match '^127\.' -or $uriHost -eq '[::1]' -or $uriHost -eq '::1' -or $uriHost -match '\.(local|localhost)$'
}

function Set-Gate0Settings {
    <#
    Prepares settings.json for the run, starting from the product owner's settings (already backed up):
    - ensures an enabled Ollama provider with id $ProviderId (added with endpoint $OllamaEndpoint and model $Model);
    - when no enabled cloud provider is configured, adds a placeholder `gate0-cloud` (openai-compatible, endpoint
      https://api.openai.com/v1, no key) so the picker has a cloud entry to grey out in Local-only mode and the
      connectivity monitor has a cloud endpoint to probe (Offline is only reported when a cloud provider is set up);
    - sets privacy.localOnly to $LocalOnly.
    Returns a description of the providers used by the checks.
    #>
    param(
        [Parameter(Mandatory)][string]$ProviderId,
        [Parameter(Mandatory)][string]$Model,
        [string]$OllamaEndpoint = 'http://localhost:11434',
        [bool]$LocalOnly = $false
    )
    $settings = Read-PillionSettings
    if ($null -eq $settings) { $settings = New-DefaultPillionSettings }
    if (-not $settings.Contains('providers') -or $null -eq $settings['providers']) { $settings['providers'] = @() }
    $providers = [System.Collections.Generic.List[object]]::new()
    foreach ($p in @($settings['providers'])) { if ($null -ne $p) { $providers.Add($p) } }

    $local = $providers | Where-Object { $_['id'] -eq $ProviderId } | Select-Object -First 1
    $addedLocal = $false
    if ($null -eq $local) {
        $local = [ordered]@{ id = $ProviderId; kind = 'ollama'; displayName = 'Ollama'; endpoint = $OllamaEndpoint; models = @($Model); enabled = $true }
        $providers.Insert(0, $local)
        $addedLocal = $true
    }
    else {
        if ($local['kind'] -ne 'ollama') { throw "Provider '$ProviderId' in settings.json has kind '$($local['kind'])', not ollama. Pass -ProviderId with the id of an Ollama provider." }
        $local['enabled'] = $true
        if (@($local['models']) -notcontains $Model) { $local['models'] = @(@($local['models']) + $Model | Where-Object { $_ }) }
    }

    $cloud = @($providers | Where-Object { $_['enabled'] -ne $false -and $_['kind'] -ne 'ollama' -and -not (Test-IsLocalEndpoint $_['endpoint']) })
    $addedPlaceholder = $false
    if ($cloud.Count -eq 0) {
        $providers.Add([ordered]@{ id = 'gate0-cloud'; kind = 'openai-compatible'; displayName = 'Gate 0 cloud placeholder'; endpoint = 'https://api.openai.com/v1'; models = @('gpt-4o-mini'); enabled = $true })
        $addedPlaceholder = $true
    }
    $settings['providers'] = $providers.ToArray()
    if (-not $settings.Contains('privacy') -or $null -eq $settings['privacy']) { $settings['privacy'] = [ordered]@{} }
    $settings['privacy']['localOnly'] = $LocalOnly
    Write-PillionSettings -Settings $settings

    $displayName = if ($local['displayName']) { $local['displayName'] } else { $ProviderId }
    $info = [ordered]@{
        localProviderId      = $ProviderId
        localProviderName    = $displayName
        localProviderAdded   = $addedLocal
        localModel           = $Model
        cloudPlaceholderAdded = $addedPlaceholder
        cloudProviderIds     = @($settings['providers'] | Where-Object { $_['kind'] -ne 'ollama' -and -not (Test-IsLocalEndpoint $_['endpoint']) } | ForEach-Object { $_['id'] })
        localOnly            = $LocalOnly
        method               = 'settings.json written before launch (atomic replace); the Options page is not driven'
    }
    Write-Gate0Log ("Settings prepared: Ollama provider '{0}' ({1}), cloud placeholder added: {2}, localOnly={3}" -f $ProviderId, ($addedLocal ? 'added' : 'already present'), $addedPlaceholder, $LocalOnly)
    return $info
}

function Set-PillionLocalOnly {
    <# Turns privacy.localOnly on or off in settings.json; Pillion Host's file watcher picks the change up. #>
    param([Parameter(Mandatory)][bool]$On)
    $settings = Read-PillionSettings
    if ($null -eq $settings) { throw 'settings.json is missing.' }
    if (-not $settings.Contains('privacy') -or $null -eq $settings['privacy']) { $settings['privacy'] = [ordered]@{} }
    $settings['privacy']['localOnly'] = $On
    Write-PillionSettings -Settings $settings
    Write-Gate0Log "privacy.localOnly set to $On in settings.json"
}

function Get-PillionProviderSettings {
    param([Parameter(Mandatory)][string]$Id)
    $settings = Read-PillionSettings
    if ($null -eq $settings) { return $null }
    return @($settings['providers']) | Where-Object { $_ -and $_['id'] -eq $Id } | Select-Object -First 1
}

#endregion

#region Experimental Instance

function Start-ExpInstance {
    <#
    Launches devenv.exe /rootsuffix Exp on a folder and waits for its main window. Pillion Host's connectivity
    monitor is set to log at Debug for this instance only (an environment variable passed to the child), so probe
    rounds show in the host log.
    #>
    param([Parameter(Mandatory)][string]$DevEnv, [Parameter(Mandatory)][string]$Folder, [int]$TimeoutSec = 180)
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $readme = Join-Path $Folder 'hello.txt'
    if (-not (Test-Path $readme)) { 'Scratch folder for the Pillion Gate 0 run.' | Set-Content -LiteralPath $readme -Encoding utf8 }

    $envName = 'Logging__LogLevel__Pillion.Common.Connectivity'
    $previous = [Environment]::GetEnvironmentVariable($envName)
    [Environment]::SetEnvironmentVariable($envName, 'Debug')
    try { $proc = Start-Process -FilePath $DevEnv -ArgumentList @('/rootsuffix', 'Exp', "`"$Folder`"") -PassThru }
    finally { [Environment]::SetEnvironmentVariable($envName, $previous) }

    $script:Gate0.ExpPid = $proc.Id
    $script:Gate0.LaunchedAt = [DateTimeOffset]::Now
    $script:Gate0.ExpProcess = $proc
    $script:Gate0.ChatElement = $null
    $script:Gate0.MainElement = $null
    $script:Gate0.HostPids.Clear()
    Write-Gate0Log "Launched the Experimental Instance: pid $($proc.Id), folder $Folder"

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "The Experimental Instance exited during start-up (exit code $($proc.ExitCode))." }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero -and $proc.MainWindowTitle -match 'Experimental Instance') {
            $script:Gate0.MainHwnd = $proc.MainWindowHandle
            $script:Gate0.MainElement = $script:AE::FromHandle($proc.MainWindowHandle)
            Write-Gate0Log "Main window ready: '$($proc.MainWindowTitle)'"
            return $proc
        }
        Start-Sleep -Milliseconds 500
    }
    throw "The Experimental Instance's main window did not appear within $TimeoutSec s."
}

function Get-ExpPid { $script:Gate0.ExpPid }

function Get-ExpMainElement {
    if ($null -eq $script:Gate0.MainElement) { throw 'The Experimental Instance has not been started.' }
    return $script:Gate0.MainElement
}

function Get-ExpHostProcess {
    <# Returns the Pillion.Host.exe whose parent is the launched devenv (or $null), and remembers its pid. #>
    if (-not $script:Gate0.ExpPid) { return $null }
    $h = Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe' AND ParentProcessId=$($script:Gate0.ExpPid)" | Select-Object -First 1
    if ($h) { [void]$script:Gate0.HostPids.Add([int]$h.ProcessId) }
    return $h
}

function Wait-ExpHostProcess {
    <# Waits for a host child of the launched devenv, optionally one whose pid differs from $NotPid. #>
    param([int]$TimeoutSec = 30, [int]$NotPid = 0)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $h = Get-ExpHostProcess
        if ($h -and $h.ProcessId -ne $NotPid) { return $h }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Get-ExpDte {
    <# The DTE of the launched instance, from the Running Object Table entry that carries its pid. #>
    if (-not $script:Gate0.ExpPid) { return $null }
    return [Gate0Native]::GetRunningObject("!VisualStudio.DTE.$($script:Gate0.VsMajor).0:$($script:Gate0.ExpPid)")
}

function Find-UiElements {
    param(
        [Parameter(Mandatory)]$Root,
        [string]$Name,
        [string]$ControlType,
        [string]$AutomationId,
        [System.Windows.Automation.TreeScope]$Scope = [System.Windows.Automation.TreeScope]::Descendants
    )
    $conds = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    if ($Name) { $conds.Add([System.Windows.Automation.PropertyCondition]::new($script:AE::NameProperty, $Name)) }
    if ($ControlType) { $conds.Add([System.Windows.Automation.PropertyCondition]::new($script:AE::ControlTypeProperty, $script:CT::$ControlType)) }
    if ($AutomationId) { $conds.Add([System.Windows.Automation.PropertyCondition]::new($script:AE::AutomationIdProperty, $AutomationId)) }
    $cond = switch ($conds.Count) {
        0 { [System.Windows.Automation.Condition]::TrueCondition }
        1 { $conds[0] }
        default { [System.Windows.Automation.AndCondition]::new($conds.ToArray()) }
    }
    return @($Root.FindAll($Scope, $cond))
}

function Wait-UiElement {
    param([Parameter(Mandatory)]$Root, [string]$Name, [string]$ControlType, [string]$AutomationId, [int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $found = @(Find-UiElements -Root $Root -Name $Name -ControlType $ControlType -AutomationId $AutomationId)
        if ($found.Count -gt 0) { return $found[0] }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "UI element not found within $TimeoutSec s: name='$Name' type='$ControlType' id='$AutomationId'."
}

function Invoke-UiElement { param([Parameter(Mandatory)]$Element) $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function Invoke-ExpMenuCommand {
    <# Opens a top-level menu of the Experimental Instance (for example Tools) and invokes one of its items by name. #>
    param([Parameter(Mandatory)][string]$Menu, [Parameter(Mandatory)][string]$Item)
    $main = Get-ExpMainElement
    $bar = Wait-UiElement -Root $main -ControlType MenuBar -TimeoutSec 15
    $top = Find-UiElements -Root $bar -Name $Menu -ControlType MenuItem -Scope Children | Select-Object -First 1
    if (-not $top) { throw "Menu '$Menu' not found in the Experimental Instance." }
    $expand = $top.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $expand.Expand()
        $deadline = (Get-Date).AddSeconds(5)
        do {
            $entry = Find-UiElements -Root $top -Name $Item -ControlType MenuItem | Select-Object -First 1
            if ($entry) { break }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $deadline)
        if ($entry) {
            if (-not $entry.Current.IsEnabled) { $expand.Collapse(); throw "Menu item '$Menu > $Item' is disabled." }
            Invoke-UiElement $entry
            Write-Gate0Log "Invoked $Menu > $Item"
            return
        }
        try { $expand.Collapse() } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Menu item '$Menu > $Item' not found."
}

function Get-ExpStatusBarText {
    <# The Visual Studio status bar text (the TextCompartment element), or $null. #>
    try {
        $e = Find-UiElements -Root (Get-ExpMainElement) -AutomationId 'TextCompartment' -ControlType Text | Select-Object -First 1
        if ($e) { return $e.Current.Name }
    }
    catch { }
    return $null
}

function Save-ExpScreenshot {
    <# Captures the Experimental Instance main window with PrintWindow (works when it is covered) into the run folder. #>
    param([Parameter(Mandatory)][string]$Name)
    try {
        $hwnd = $script:Gate0.MainHwnd
        $rect = [Gate0Native+RECT]::new()
        [void][Gate0Native]::GetWindowRect($hwnd, [ref]$rect)
        $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
        if ($w -le 0 -or $h -le 0) { return $null }
        $bmp = [System.Drawing.Bitmap]::new($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][Gate0Native]::PrintWindow($hwnd, $hdc, 2)
        $g.ReleaseHdc($hdc); $g.Dispose()
        $dir = Join-Path $script:Gate0.RunDir 'screenshots'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $path = Join-Path $dir "$Name.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        return $path
    }
    catch { Write-Gate0Log "Screenshot '$Name' failed: $($_.Exception.Message)" -Level warn; return $null }
}

function Stop-ExpInstance {
    <# Closes the launched instance (main window close, then DTE.Quit, then kill as the last resort). Returns how. #>
    param([int]$TimeoutSec = 60)
    $proc = $script:Gate0.ExpProcess
    if ($null -eq $proc) { return 'not started' }
    $proc.Refresh()
    if ($proc.HasExited) { return 'already exited' }
    [void]$proc.CloseMainWindow()
    if ($proc.WaitForExit($TimeoutSec * 1000)) { Write-Gate0Log 'Experimental Instance closed (main window close).'; return 'main window close' }
    try { $dte = Get-ExpDte; if ($dte) { $dte.Quit() } } catch { Write-Gate0Log "DTE.Quit failed: $($_.Exception.Message)" -Level warn }
    if ($proc.WaitForExit(30000)) { Write-Gate0Log 'Experimental Instance closed (DTE.Quit).'; return 'DTE.Quit' }
    Write-Gate0Log 'Experimental Instance did not close; killing it (only the launched pid).' -Level warn
    Stop-Process -Id $proc.Id -Force
    [void]$proc.WaitForExit(15000)
    return 'killed'
}

function Test-NoHostSurvivors {
    <# Every Pillion.Host pid seen as a child of the launched devenv must be gone. #>
    param([int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $alive = @($script:Gate0.HostPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        $children = @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe' AND ParentProcessId=$($script:Gate0.ExpPid)")
        if ($alive.Count -eq 0 -and $children.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    [ordered]@{ hostPidsSeen = @($script:Gate0.HostPids); survivors = @((@($alive) + @($children | ForEach-Object { [int]$_.ProcessId })) | Select-Object -Unique) }
}

#endregion

#region Chat window

function Open-PillionChat {
    <# Opens Pillion Chat through Tools > Pillion: Open Chat and returns its UI Automation element. #>
    Invoke-ExpMenuCommand -Menu 'Tools' -Item 'Pillion: Open Chat'
    $chat = Wait-UiElement -Root (Get-ExpMainElement) -Name 'Pillion Chat' -ControlType Custom -TimeoutSec 30
    $script:Gate0.ChatElement = $chat
    return $chat
}

function Get-ChatElement {
    $chat = $script:Gate0.ChatElement
    if ($null -ne $chat) {
        try { [void]$chat.Current.Name; return $chat } catch { $script:Gate0.ChatElement = $null }
    }
    $chat = Wait-UiElement -Root (Get-ExpMainElement) -Name 'Pillion Chat' -ControlType Custom -TimeoutSec 15
    $script:Gate0.ChatElement = $chat
    return $chat
}

function Get-ChatStatusElement {
    $chat = Get-ChatElement
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $e = Find-UiElements -Root $chat -ControlType Text | Where-Object { $_.Current.Name -like 'Pillion Host status:*' } | Select-Object -First 1
        if ($e) { return $e }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw 'The chat status line was not found.'
}

function Get-ChatStatusText {
    $name = (Get-ChatStatusElement).Current.Name
    return ($name -replace '^Pillion Host status:\s*', '')
}

function Get-HostStateFromStatus {
    <# Classifies the chat status line (Ready, Restarting, Starting, Failed) and the connectivity it names. #>
    param([string]$Status)
    $state = switch -Regex ($Status) {
        '^Ready' { 'Ready'; break }
        'restarting' { 'Restarting'; break }
        '^Starting' { 'Starting'; break }
        'failed' { 'Failed'; break }
        default { 'Unknown' }
    }
    $connectivity = switch -Regex ($Status) {
        'Local-only' { 'Local-only'; break }
        'Offline' { 'Offline'; break }
        'Degraded' { 'Degraded'; break }
        'Online' { 'Online'; break }
        default { $null }
    }
    [pscustomobject]@{ State = $state; Connectivity = $connectivity }
}

function Wait-ChatStatus {
    <# Polls the chat status line until the predicate holds. Returns the matching status text and the wait time. #>
    param([Parameter(Mandatory)][scriptblock]$Until, [int]$TimeoutSec = 60, [string]$What = 'status')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $seen = [System.Collections.Generic.List[string]]::new()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $text = Get-ChatStatusText
            if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $text) { $seen.Add($text) }
            if (& $Until $text) { return [pscustomobject]@{ Status = $text; WaitMs = [int]$sw.ElapsedMilliseconds; Seen = $seen.ToArray() } }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out after $TimeoutSec s waiting for $What; status line went through: $($seen -join ' -> ')"
}

function Get-ChatControl {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$ControlType)
    return Wait-UiElement -Root (Get-ChatElement) -Name $Name -ControlType $ControlType -TimeoutSec 10
}

function Get-ChatPickerEntries {
    <# Expands the model picker and returns every entry: name (the screen-reader name, with the reason when unavailable), group, enabled. #>
    $combo = Get-ChatControl -Name 'Model' -ControlType ComboBox
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    try {
        Start-Sleep -Milliseconds 400
        $entries = foreach ($group in (Find-UiElements -Root $combo -ControlType Group)) {
            foreach ($item in (Find-UiElements -Root $group -ControlType ListItem)) {
                [pscustomobject]@{ Group = $group.Current.Name; Name = $item.Current.Name; IsEnabled = $item.Current.IsEnabled }
            }
        }
        return @($entries)
    }
    finally { try { $ec.Collapse() } catch { } }
}

function Select-ChatModel {
    <# Selects the first enabled picker entry whose screen-reader name starts with $NamePrefix. Returns its name. #>
    param([Parameter(Mandatory)][string]$NamePrefix, [int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $combo = Get-ChatControl -Name 'Model' -ControlType ComboBox
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $names = @()
    do {
        $ec.Expand()
        try {
            Start-Sleep -Milliseconds 300
            $items = Find-UiElements -Root $combo -ControlType ListItem
            $names = @($items | ForEach-Object { $_.Current.Name })
            $match = $items | Where-Object { $_.Current.Name.StartsWith($NamePrefix, [StringComparison]::OrdinalIgnoreCase) -and $_.Current.IsEnabled } | Select-Object -First 1
            if ($match) {
                $match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                $selectedName = $match.Current.Name
            }
        }
        finally { try { $ec.Collapse() } catch { } }
        if ($match) {
            Start-Sleep -Milliseconds 200
            $selected = @($combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name })
            if ($selected -contains $selectedName) { Write-Gate0Log "Picked model: $selectedName"; return $selectedName }
        }
        # The list may still be loading after a (re)start: ask for it again.
        try { Invoke-UiElement (Get-ChatControl -Name 'Refresh models' -ControlType Button) } catch { }
        Start-Sleep -Milliseconds 1000
    } while ((Get-Date) -lt $deadline)
    throw "No enabled model in the picker starts with '$NamePrefix' (entries: $($names -join ' | '))."
}

function Get-ChatTurns {
    <#
    Reads the transcript: each turn is a header Text ("You" or the model that answered) followed by its text (an Edit
    control for the developer's message; Document controls, one per Markdown block, and code views for a reply) and any
    notes (Answering..., Stopped., Interrupted, Error: ...). The transcript virtualises, so only turns in view are read.
    #>
    $chat = Get-ChatElement
    $scroller = Find-UiElements -Root $chat -AutomationId 'TranscriptScroller' | Select-Object -First 1
    if (-not $scroller) { return @() }
    $cond = [System.Windows.Automation.OrCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($script:AE::ControlTypeProperty, $script:CT::Text),
        [System.Windows.Automation.PropertyCondition]::new($script:AE::ControlTypeProperty, $script:CT::Edit),
        [System.Windows.Automation.PropertyCondition]::new($script:AE::ControlTypeProperty, $script:CT::Document))
    $turns = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($e in $scroller.FindAll($script:TreeScope::Descendants, $cond)) {
        $type = $e.Current.ControlType
        $name = $e.Current.Name
        if ($type -eq $script:CT::Edit -or $type -eq $script:CT::Document) {
            if ($current) {
                $value = $null
                $pattern = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $value = $pattern.Current.Value }
                elseif ($e.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) { $value = $pattern.DocumentRange.GetText(-1).TrimEnd() }
                if ($null -ne $value) { $current.Segments.Add($value) }
            }
            continue
        }
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $isNote = $name -match '^(Answering\.\.\.|Stopped\.|Interrupted:|Error:)' -or ($current -and $current.Notes.Count -gt 0 -and $current.Notes[$current.Notes.Count - 1] -like 'Error:*')
        $isHeader = -not $isNote -and ($name -eq 'You' -or $name -match ' via |, (local|cloud)$|^Auto \(')
        if ($isHeader -or -not $current) {
            $current = [pscustomobject]@{ Header = $name; IsUser = ($name -eq 'You'); Segments = [System.Collections.Generic.List[string]]::new(); Notes = [System.Collections.Generic.List[string]]::new() }
            $turns.Add($current)
        }
        else { $current.Notes.Add($name) }
    }
    return $turns.ToArray()
}


function Clear-ChatConversation {
    try {
        $clear = Get-ChatControl -Name 'Clear conversation' -ControlType Button
        if ($clear.Current.IsEnabled) { Invoke-UiElement $clear; Start-Sleep -Milliseconds 300 }
    }
    catch { }
}

function Send-ChatMessage {
    <#
    Types a message into Pillion Chat, presses Send and waits for the reply to finish (Stop disabled and no
    "Answering..." note). Returns the reply's header, text, notes and timings.
    #>
    param([Parameter(Mandatory)][string]$Text, [int]$TimeoutSec = 180)
    $chat = Get-ChatElement
    $inputBox = Get-ChatControl -Name 'Message' -ControlType Edit
    $send = Get-ChatControl -Name 'Send' -ControlType Button
    $stop = Get-ChatControl -Name 'Stop' -ControlType Button
    $before = @(Get-ChatTurns).Count
    $inputBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    $deadline = (Get-Date).AddSeconds(15)
    while (-not $send.Current.IsEnabled) {
        if ((Get-Date) -gt $deadline) { throw "Send stayed disabled (status: $(Get-ChatStatusText))." }
        Start-Sleep -Milliseconds 200
    }
    $sentAt = [DateTimeOffset]::Now
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-UiElement $send
    $firstTextMs = $null
    $reply = $null
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Milliseconds 250
        $turns = @(Get-ChatTurns)
        # The transcript virtualises: once it is longer than the view the number of turns in view stops growing, so the
        # new reply is also recognised by the message just above it.
        $promptAbove = $turns.Count -ge 2 -and $turns[-2].IsUser -and ($turns[-2].Segments -join "`n").Trim() -eq $Text.Trim()
        if ($turns.Count -lt $before + 2 -and -not $promptAbove) { continue }
        $reply = $turns[-1]
        if ($null -eq $firstTextMs -and ($reply.Segments -join '').Length -gt 0) { $firstTextMs = [int]$sw.ElapsedMilliseconds }
        $streaming = $reply.Notes -contains 'Answering...'
        if (-not $streaming -and -not $stop.Current.IsEnabled) { break }
    }
    $sw.Stop()
    if ($null -eq $reply) { throw "No reply turn appeared within $TimeoutSec s." }
    $result = [pscustomobject]@{
        Prompt      = $Text
        Header      = $reply.Header
        Reply       = ($reply.Segments -join "`n").Trim()
        Notes       = @($reply.Notes)
        SentAt      = $sentAt
        FirstTextMs = $firstTextMs
        TotalMs     = [int]$sw.ElapsedMilliseconds
        Finished    = -not ($reply.Notes -contains 'Answering...') -and -not $stop.Current.IsEnabled
    }
    Write-Gate0Log ("Reply under '{0}' in {1:n1} s: '{2}'{3}" -f $result.Header, ($result.TotalMs / 1000), $result.Reply, ($result.Notes.Count ? " notes: $($result.Notes -join '; ')" : ''))
    return $result
}

#endregion

#region Logs

function Read-SharedLines {
    param([Parameter(Mandatory)][string]$Path)
    $fs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $sr = [IO.StreamReader]::new($fs)
        $lines = [System.Collections.Generic.List[string]]::new()
        while ($null -ne ($l = $sr.ReadLine())) { $lines.Add($l) }
        return $lines.ToArray()
    }
    finally { $fs.Dispose() }
}

function Select-LogFiles {
    <#
    The log files that can hold lines written since $Since, chosen by the date in their names (host-YYYYMMDD[-pid].log,
    vsix-YYYYMMDD.log). Not by LastWriteTime: NTFS does not update it while the writer keeps the file open, so a host
    that has been running for a while would look idle.
    #>
    param([Parameter(Mandatory)][string]$Prefix, [Parameter(Mandatory)][DateTimeOffset]$Since)
    $from = $Since.AddDays(-1).ToString('yyyyMMdd')
    Get-ChildItem -Path $script:LogDir -Filter "$Prefix-*.log" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^$Prefix-(\d{8})" -and $Matches[1] -ge $from }
}

function Get-HostLogEntries {
    <# Parsed JSON lines from Pillion Host's logs (host-*.log) with a timestamp at or after $Since. #>
    param([Parameter(Mandatory)][DateTimeOffset]$Since, [int]$EventId = -1)
    if (-not (Test-Path $script:LogDir)) { return @() }
    $files = Select-LogFiles -Prefix 'host' -Since $Since
    $entries = foreach ($f in $files) {
        foreach ($line in (Read-SharedLines $f.FullName)) {
            if (-not $line.StartsWith('{')) { continue }
            try { $o = $line | ConvertFrom-Json -DateKind String } catch { continue }
            $ts = [DateTimeOffset]::Parse($o.ts, [Globalization.CultureInfo]::InvariantCulture)
            if ($ts -lt $Since) { continue }
            if ($EventId -ge 0 -and (-not ($o.PSObject.Properties.Name -contains 'eventId') -or $o.eventId -ne $EventId)) { continue }
            $o | Add-Member -NotePropertyName timestamp -NotePropertyValue $ts -PassThru
        }
    }
    return @($entries | Sort-Object timestamp)
}

function Get-OutboundEntries {
    <# Outbound log lines (event 420) since $Since, waiting up to $WaitSec for at least $Min of them. #>
    param([Parameter(Mandatory)][DateTimeOffset]$Since, [int]$Min = 1, [int]$WaitSec = 15)
    $deadline = (Get-Date).AddSeconds($WaitSec)
    do {
        $entries = @(Get-HostLogEntries -Since $Since -EventId 420)
        if ($entries.Count -ge $Min) { return $entries }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $files = @(Get-ChildItem -Path $script:LogDir -Filter 'host-*.log' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) ($($_.LastWriteTime.ToString('HH:mm:ss.fff')))" })
    Write-Gate0Log ("No Outbound line since {0:o} after {1} s; host logs: {2}" -f $Since, $WaitSec, ($files -join ', ')) -Level warn
    return $entries
}

function Get-VsixLogEntries {
    <# Lines of the extension's log (vsix-*.log, UTC timestamps) at or after $Since, optionally for given events. #>
    param([Parameter(Mandatory)][DateTimeOffset]$Since, [string[]]$Event)
    if (-not (Test-Path $script:LogDir)) { return @() }
    $files = Select-LogFiles -Prefix 'vsix' -Since $Since
    $entries = foreach ($f in $files) {
        foreach ($line in (Read-SharedLines $f.FullName)) {
            if ($line -notmatch '^(?<ts>\S+)\s+(?<level>\S+)\s+(?<event>\S+)\s*(?<rest>.*)$') { continue }
            try { $ts = [DateTimeOffset]::Parse($Matches.ts, [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
            if ($ts -lt $Since) { continue }
            if ($Event -and $Event -notcontains $Matches.event) { continue }
            [pscustomobject]@{ timestamp = $ts; level = $Matches.level; event = $Matches.event; text = $line }
        }
    }
    return @($entries | Sort-Object timestamp)
}

function Save-LogExcerpts {
    <# Copies the host and extension log lines written since $Since into the run folder. #>
    param([Parameter(Mandatory)][DateTimeOffset]$Since)
    $hostLines = Get-HostLogEntries -Since $Since | ForEach-Object { '{0} {1} [{2}] {3}' -f $_.ts, $_.level, $_.category, $_.message }
    $vsixLines = Get-VsixLogEntries -Since $Since | ForEach-Object { $_.text }
    $hostPath = Join-Path $script:Gate0.RunDir 'host-log-excerpt.txt'
    $vsixPath = Join-Path $script:Gate0.RunDir 'vsix-log-excerpt.txt'
    Set-Content -LiteralPath $hostPath -Value $hostLines -Encoding utf8
    Set-Content -LiteralPath $vsixPath -Value $vsixLines -Encoding utf8
    [ordered]@{ hostLog = $hostPath; hostLines = @($hostLines).Count; vsixLog = $vsixPath; vsixLines = @($vsixLines).Count }
}

#endregion

#region Responsiveness probe

function Measure-ExpResponsiveness {
    <#
    Polls the Experimental Instance every $IntervalMs for $DurationSec: a UI Automation property of the main window
    (its Name, served by Visual Studio's UI thread), the chat status line and the status bar text, plus WM_NULL
    through SendMessageTimeout. Returns the longest gap between two answered polls, the slowest single call, and the
    sequence of status texts with times relative to $Origin.
    #>
    param(
        [Parameter(Mandatory)][Diagnostics.Stopwatch]$Clock,
        [double]$OriginMs = 0,
        [int]$DurationSec = 15,
        [int]$IntervalMs = 250,
        [scriptblock]$StopWhen
    )
    $main = Get-ExpMainElement
    $status = Get-ChatStatusElement
    $statusBar = Find-UiElements -Root $main -AutomationId 'TextCompartment' -ControlType Text | Select-Object -First 1
    $hwnd = $script:Gate0.MainHwnd
    $samples = [System.Collections.Generic.List[object]]::new()
    $sequence = [System.Collections.Generic.List[object]]::new()
    $barSequence = [System.Collections.Generic.List[object]]::new()
    $lastAnswered = $Clock.Elapsed.TotalMilliseconds
    $longestGap = 0.0; $slowest = 0.0; $failures = 0; $pingFailures = 0
    $end = $Clock.Elapsed.TotalMilliseconds + $DurationSec * 1000
    $stoppedEarly = $false
    while ($Clock.Elapsed.TotalMilliseconds -lt $end) {
        $tickStart = $Clock.Elapsed.TotalMilliseconds
        $ok = $true
        $text = $null; $bar = $null
        try {
            [void]$main.Current.Name
            $text = $status.Current.Name -replace '^Pillion Host status:\s*', ''
            if ($statusBar) { $bar = $statusBar.Current.Name }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            try { $status = Get-ChatStatusElement; $text = $status.Current.Name -replace '^Pillion Host status:\s*', '' } catch { $ok = $false }
        }
        catch { $ok = $false }
        $uiaDone = $Clock.Elapsed.TotalMilliseconds
        $ping = [Gate0Native]::Ping($hwnd, 2000)
        $tickEnd = $Clock.Elapsed.TotalMilliseconds
        if (-not $ping) { $pingFailures++ }
        $callMs = $uiaDone - $tickStart
        if ($ok) {
            $gap = $uiaDone - $lastAnswered
            if ($gap -gt $longestGap) { $longestGap = $gap }
            $lastAnswered = $uiaDone
        }
        else { $failures++ }
        if ($callMs -gt $slowest) { $slowest = $callMs }
        $rel = [int]($tickStart - $OriginMs)
        $samples.Add([pscustomobject]@{ tMs = $rel; uiaMs = [math]::Round($callMs, 1); pingMs = [math]::Round($tickEnd - $uiaDone, 1); ok = $ok; ping = $ping })
        if ($null -ne $text -and ($sequence.Count -eq 0 -or $sequence[$sequence.Count - 1].status -ne $text)) { $sequence.Add([pscustomobject]@{ tMs = $rel; status = $text }) }
        if ($null -ne $bar -and ($barSequence.Count -eq 0 -or $barSequence[$barSequence.Count - 1].status -ne $bar)) { $barSequence.Add([pscustomobject]@{ tMs = $rel; status = $bar }) }
        if ($StopWhen -and $null -ne $text -and (& $StopWhen $text $rel)) { $stoppedEarly = $true; break }
        $sleep = $IntervalMs - ($Clock.Elapsed.TotalMilliseconds - $tickStart)
        if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]$sleep) }
    }
    # A gap still open at the end counts too.
    $tail = $Clock.Elapsed.TotalMilliseconds - $lastAnswered
    if ($tail -gt $longestGap) { $longestGap = $tail }
    [pscustomobject]@{
        samples           = $samples.Count
        intervalMs        = $IntervalMs
        longestGapMs      = [int][math]::Round($longestGap)
        slowestCallMs     = [int][math]::Round($slowest)
        failedPolls       = $failures
        wmNullTimeouts    = $pingFailures
        statusSequence    = $sequence.ToArray()
        statusBarSequence = $barSequence.ToArray()
        stoppedEarly      = $stoppedEarly
        rawSamples        = $samples.ToArray()
    }
}

#endregion

#region Checks (steps d to h)

function Invoke-ExpLaunchCheck {
    <# Step d: launch the instance on the scratch folder, open Pillion Chat and wait for Ready. #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$Folder, [string]$ExpectConnectivity)
    $proc = Start-ExpInstance -DevEnv $VisualStudio.DevEnv -Folder $Folder
    $hostProc = Wait-ExpHostProcess -TimeoutSec 90
    if (-not $hostProc) { throw 'Pillion Host did not start as a child of the Experimental Instance within 90 s.' }
    $chat = Open-PillionChat
    $ready = Wait-ChatStatus -Until { param($s) $s -match '^Ready' } -TimeoutSec 90 -What 'Ready'
    $details = [ordered]@{
        Summary       = "pid $($proc.Id); host pid $($hostProc.ProcessId); chat status '$($ready.Status)'"
        devenvPid     = $proc.Id
        hostPid       = [int]$hostProc.ProcessId
        hostPath      = $hostProc.ExecutablePath
        chatStatus    = $ready.Status
        statusBar     = Get-ExpStatusBarText
        windowTitle   = $proc.MainWindowTitle
    }
    if ($ExpectConnectivity) {
        $conn = Wait-ChatStatus -Until { param($s) (Get-HostStateFromStatus $s).Connectivity -eq $ExpectConnectivity } -TimeoutSec 90 -What "connectivity $ExpectConnectivity"
        $details['connectivityWaitMs'] = $conn.WaitMs
        $details['chatStatus'] = $conn.Status
    }
    return $details
}

function Invoke-LocalChatCheck {
    <# Step e: pick the local model, send the prompt, check the header names it via the provider, local; record the Outbound line. #>
    param([Parameter(Mandatory)][string]$Model, [Parameter(Mandatory)][string]$ProviderName, [string]$Prompt = 'Reply with the single word pong')
    Clear-ChatConversation
    $picked = Select-ChatModel -NamePrefix "$Model ($ProviderName)"
    $since = [DateTimeOffset]::Now.AddSeconds(-1)
    $reply = Send-ChatMessage -Text $Prompt
    $expectedHeader = "$Model via $ProviderName, local"
    $outbound = @(Get-OutboundEntries -Since $since)
    $line = $outbound | Select-Object -Last 1
    $routed = @(Get-HostLogEntries -Since $since -EventId 430) | Select-Object -Last 1
    $lineText = if ($line) { $line.message } else { '(none found)' }
    $routedText = if ($routed) { $routed.message } else { $null }
    $shot = Save-ExpScreenshot -Name 'local-chat'
    $headerOk = $reply.Header -eq $expectedHeader
    $outboundOk = $null -ne $line -and $line.state.Locality -eq 'local' -and $line.state.ModelId -eq $Model -and $line.state.Outcome -eq 'Succeeded'
    $ok = $headerOk -and $reply.Finished -and $reply.Reply.Length -gt 0 -and $outboundOk -and -not ($reply.Notes | Where-Object { $_ -like 'Error:*' -or $_ -like 'Interrupted*' })
    [ordered]@{
        Status          = $ok ? 'Pass' : 'Fail'
        Summary         = "header '$($reply.Header)'; reply '$($reply.Reply)'; outbound '$lineText'"
        pickedEntry     = $picked
        prompt          = $Prompt
        header          = $reply.Header
        expectedHeader  = $expectedHeader
        reply           = $reply.Reply
        replyMentionsPong = $reply.Reply -match 'pong'
        notes           = $reply.Notes
        firstTextMs     = $reply.FirstTextMs
        totalMs         = $reply.TotalMs
        chatStatus      = Get-ChatStatusText
        outboundLine    = $lineText
        outboundCount   = $outbound.Count
        routedLine      = $routedText
        screenshot      = $shot
    }
}

function Invoke-HostKillOnce {
    <# Kills the instance's Pillion.Host and measures UI responsiveness and the status sequence for $ObserveSec. #>
    param([Parameter(Mandatory)][int]$Index, [int]$ObserveSec = 15, [switch]$ExpectFailed)
    $victim = Get-ExpHostProcess
    if (-not $victim) { throw "Kill $Index : no Pillion.Host child of the Experimental Instance is running." }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $killedAt = [DateTimeOffset]::Now
    Stop-Process -Id $victim.ProcessId -Force
    $origin = $clock.Elapsed.TotalMilliseconds
    Write-Gate0Log "Kill $Index : killed Pillion.Host pid $($victim.ProcessId)"
    $probe = Measure-ExpResponsiveness -Clock $clock -OriginMs $origin -DurationSec $ObserveSec -IntervalMs 250
    # Time to Ready: the first Ready after a status that was not Ready.
    $sawNotReady = $false; $readyMs = $null; $failedMs = $null
    foreach ($s in $probe.statusSequence) {
        $state = (Get-HostStateFromStatus $s.status).State
        if ($state -ne 'Ready') { $sawNotReady = $true }
        if ($state -eq 'Failed' -and $null -eq $failedMs) { $failedMs = $s.tMs }
        if ($sawNotReady -and $state -eq 'Ready' -and $null -eq $readyMs) { $readyMs = $s.tMs }
    }
    $newHost = if ($ExpectFailed) { Get-ExpHostProcess } else { Wait-ExpHostProcess -TimeoutSec 5 -NotPid $victim.ProcessId }
    $transitions = @(Get-VsixLogEntries -Since $killedAt.AddSeconds(-1) -Event 'supervisor.state', 'host.exited', 'host.process.started' | ForEach-Object { $_.text })
    [ordered]@{
        index             = $Index
        killedPid         = [int]$victim.ProcessId
        killedAt          = $killedAt.ToString('o')
        newHostPid        = if ($newHost -and $newHost.ProcessId -ne $victim.ProcessId) { [int]$newHost.ProcessId } else { $null }
        longestGapMs      = $probe.longestGapMs
        slowestCallMs     = $probe.slowestCallMs
        failedPolls       = $probe.failedPolls
        wmNullTimeouts    = $probe.wmNullTimeouts
        samples           = $probe.samples
        statusSequence    = $probe.statusSequence
        statusBarSequence = $probe.statusBarSequence
        timeToReadyMs     = $readyMs
        timeToFailedMs    = $failedMs
        vsixLog           = $transitions
    }
}

function Invoke-HostKillCheck {
    <#
    Step f: three kills of Pillion.Host within five minutes. Kills 1 and 2: VS keeps answering (longest gap under
    $MaxGapMs), the status goes Restarting then Ready, and a chat message is answered afterwards. Kill 3: the
    supervisor goes to Failed with the Open Logs hint. Then Tools > Pillion: Retry brings it back to Ready.
    #>
    param([Parameter(Mandatory)][string]$Model, [Parameter(Mandatory)][string]$ProviderName, [int]$MaxGapMs = 2000, [string]$Prompt = 'Reply with the single word pong')
    $kills = [System.Collections.Generic.List[object]]::new()
    $problems = [System.Collections.Generic.List[string]]::new()
    $firstKill = $null

    for ($i = 1; $i -le 2; $i++) {
        $k = Invoke-HostKillOnce -Index $i -ObserveSec 15
        if ($null -eq $firstKill) { $firstKill = [DateTimeOffset]::Parse($k.killedAt) }
        if ($k.longestGapMs -ge $MaxGapMs) { $problems.Add("kill ${i}: longest UI gap $($k.longestGapMs) ms") }
        if ($k.wmNullTimeouts -gt 0) { $problems.Add("kill ${i}: $($k.wmNullTimeouts) WM_NULL timeouts") }
        $states = @($k.statusSequence | ForEach-Object { (Get-HostStateFromStatus $_.status).State })
        if ($states -notcontains 'Restarting') { $problems.Add("kill ${i}: Restarting never shown") }
        if ($null -eq $k.timeToReadyMs) {
            # Not Ready within the observation window: keep waiting (without widening the responsiveness window).
            $late = Wait-ChatStatus -Until { param($s) $s -match '^Ready' } -TimeoutSec 30 -What "Ready after kill $i"
            $k['timeToReadyMs'] = "over 15000 (Ready $($late.WaitMs) ms after the window)"
            $problems.Add("kill ${i}: not Ready within 15 s")
        }
        $reply = Send-ChatAfterRestart -Model $Model -ProviderName $ProviderName -Prompt $Prompt
        $k['chatAfter'] = [ordered]@{ header = $reply.Header; reply = $reply.Reply; notes = $reply.Notes; totalMs = $reply.TotalMs }
        if (-not $reply.Finished -or $reply.Reply.Length -eq 0 -or ($reply.Notes | Where-Object { $_ -like 'Error:*' })) { $problems.Add("kill ${i}: no clean reply afterwards") }
        $kills.Add($k)
    }

    $k3 = Invoke-HostKillOnce -Index 3 -ObserveSec 15 -ExpectFailed
    $elapsed = ([DateTimeOffset]::Parse($k3.killedAt) - $firstKill).TotalSeconds
    if ($k3.longestGapMs -ge $MaxGapMs) { $problems.Add("kill 3: longest UI gap $($k3.longestGapMs) ms") }
    $failedStatus = Wait-ChatStatus -Until { param($s) (Get-HostStateFromStatus $s).State -eq 'Failed' } -TimeoutSec 15 -What 'Failed after the third kill'
    $barDeadline = (Get-Date).AddSeconds(10)
    do { $statusBar = Get-ExpStatusBarText; if ($statusBar -like 'Pillion: Failed*') { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $barDeadline)
    $openLogs = @(Find-UiElements -Root (Get-ChatElement) -Name 'Open Logs' -ControlType Hyperlink).Count -gt 0
    Start-Sleep -Seconds 2
    $hostAfterFailed = Get-ExpHostProcess
    $shotFailed = Save-ExpScreenshot -Name 'host-failed'
    $k3['failedChatStatus'] = $failedStatus.Status
    $k3['failedStatusBar'] = $statusBar
    $k3['openLogsLinkShown'] = $openLogs
    $k3['hostRunningWhileFailed'] = [bool]$hostAfterFailed
    $k3['screenshot'] = $shotFailed
    $kills.Add($k3)
    if ($elapsed -ge 300) { $problems.Add("three kills took $([int]$elapsed) s, not within five minutes") }
    if ($statusBar -notlike 'Pillion: Failed*Open Logs*') { $problems.Add("status bar after kill 3 reads '$statusBar'") }
    if (-not $openLogs) { $problems.Add('the chat window does not show Open Logs after Failed') }
    if ($hostAfterFailed) { $problems.Add("a host (pid $($hostAfterFailed.ProcessId)) is running while Failed") }

    # Retry through the Tools menu.
    $retryClock = [Diagnostics.Stopwatch]::StartNew()
    Invoke-ExpMenuCommand -Menu 'Tools' -Item 'Pillion: Retry'
    $ready = Wait-ChatStatus -Until { param($s) $s -match '^Ready' } -TimeoutSec 60 -What 'Ready after Retry'
    $retryMs = [int]$retryClock.ElapsedMilliseconds
    $hostAfterRetry = Wait-ExpHostProcess -TimeoutSec 10
    $replyAfterRetry = Send-ChatAfterRestart -Model $Model -ProviderName $ProviderName -Prompt $Prompt
    if (-not $replyAfterRetry.Finished -or $replyAfterRetry.Reply.Length -eq 0) { $problems.Add('no clean reply after Retry') }

    $gaps = @($kills | ForEach-Object { $_.longestGapMs })
    [ordered]@{
        Status              = ($problems.Count -eq 0) ? 'Pass' : 'Fail'
        Summary             = ("longest UI gap {0} ms (max of 3 kills, limit {1}); Ready after kill 1 in {2} ms, kill 2 in {3} ms; kill 3 -> '{4}' / status bar '{5}', Open Logs shown: {6}; Retry -> Ready in {7} ms; 3 kills in {8:n0} s" -f ($gaps | Measure-Object -Maximum).Maximum, $MaxGapMs, $kills[0].timeToReadyMs, $kills[1].timeToReadyMs, $failedStatus.Status, $statusBar, $openLogs, $retryMs, $elapsed)
        problems            = $problems.ToArray()
        maxLongestGapMs     = ($gaps | Measure-Object -Maximum).Maximum
        threeKillsWithinSec = [math]::Round($elapsed, 1)
        kills               = $kills.ToArray()
        retry               = [ordered]@{ method = 'Tools > Pillion: Retry (UI Automation menu invoke)'; timeToReadyMs = $retryMs; status = $ready.Status; statusSequence = $ready.Seen; hostPid = $(if ($hostAfterRetry) { [int]$hostAfterRetry.ProcessId } else { $null }); chatAfter = [ordered]@{ header = $replyAfterRetry.Header; reply = $replyAfterRetry.Reply } }
    }
}

function Send-ChatAfterRestart {
    param([Parameter(Mandatory)][string]$Model, [Parameter(Mandatory)][string]$ProviderName, [string]$Prompt = 'Reply with the single word pong')
    [void](Wait-ChatStatus -Until { param($s) $s -match '^Ready' } -TimeoutSec 30 -What 'Ready before sending')
    Clear-ChatConversation
    [void](Select-ChatModel -NamePrefix "$Model ($ProviderName)")
    return Send-ChatMessage -Text $Prompt
}

function Invoke-LocalOnlyCheck {
    <#
    Step g: turn Local-only on (settings.json; Pillion Host reloads it), confirm the status reads Local-only and every
    cloud entry in the picker is greyed with the Local-only reason, send a message with Auto and confirm the Outbound
    log shows only local requests and no probe ran while Local-only was on; then turn it off again.
    #>
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$ProviderName,
        [int]$HoldSec = 65,
        [switch]$AlreadyOn,
        [string]$Prompt = 'Reply with the single word pong'
    )
    $problems = [System.Collections.Generic.List[string]]::new()
    $runStart = if ($script:Gate0.LaunchedAt) { $script:Gate0.LaunchedAt } else { [DateTimeOffset]::Now.AddMinutes(-5) }
    $probesBefore = @(Get-HostLogEntries -Since $runStart | Where-Object { $_.message -like 'Probed provider*' }).Count
    $onAt = [DateTimeOffset]::Now
    if (-not $AlreadyOn) { Set-PillionLocalOnly -On $true }
    $status = Wait-ChatStatus -Until { param($s) (Get-HostStateFromStatus $s).Connectivity -eq 'Local-only' } -TimeoutSec 30 -What 'Local-only'
    # The status bar is shared with Visual Studio's own messages, so give it a few seconds to show Pillion's text.
    $barDeadline = (Get-Date).AddSeconds(10)
    do { $bar = Get-ExpStatusBarText; if ($bar -like '*Local-only*') { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $barDeadline)
    if ($bar -notlike '*Local-only*') { $problems.Add("status bar reads '$bar'") }
    Invoke-UiElement (Get-ChatControl -Name 'Refresh models' -ControlType Button)
    Start-Sleep -Milliseconds 1500
    $entries = @(Get-ChatPickerEntries)
    $cloud = @($entries | Where-Object { $_.Group -eq 'Cloud' })
    if ($cloud.Count -eq 0) { $problems.Add('the picker has no cloud entry to grey out') }
    $notGreyed = @($cloud | Where-Object { $_.IsEnabled -or $_.Name -notlike '*unavailable: Local-only mode is on*' })
    if ($notGreyed.Count -gt 0) { $problems.Add("cloud entries not greyed with the Local-only reason: $($notGreyed.Name -join ' | ')") }
    $local = @($entries | Where-Object { $_.Group -eq 'Local' -and $_.IsEnabled })
    if ($local.Count -eq 0) { $problems.Add('no local model is available in Local-only mode') }

    Clear-ChatConversation
    $picked = Select-ChatModel -NamePrefix 'Auto'
    $reply = Send-ChatMessage -Text $Prompt
    if ($reply.Header -notlike '*, local') { $problems.Add("Auto reply came from '$($reply.Header)'") }
    $shot = Save-ExpScreenshot -Name 'local-only'

    # Hold Local-only for a full probe cadence (60 s) before turning it off, so "no probes" means something.
    $remaining = $HoldSec - ([DateTimeOffset]::Now - $onAt).TotalSeconds
    if ($remaining -gt 0) { Write-Gate0Log ("Holding Local-only for another {0:n0} s (one probe cadence)" -f $remaining); Start-Sleep -Seconds ([int][math]::Ceiling($remaining)) }
    $offAt = [DateTimeOffset]::Now
    $outbound = @(Get-OutboundEntries -Since $onAt)
    $cloudOutbound = @($outbound | Where-Object { $_.state.Locality -ne 'local' })
    if ($outbound.Count -eq 0) { $problems.Add('no Outbound line was written for the Local-only message') }
    if ($cloudOutbound.Count -gt 0) { $problems.Add("cloud requests while Local-only: $($cloudOutbound.message -join ' | ')") }
    # Probe rounds already started may finish within their 2 s timeout, so count from 3 s after the switch.
    $probesDuring = @(Get-HostLogEntries -Since $onAt.AddSeconds(3) | Where-Object { $_.timestamp -lt $offAt -and $_.message -like 'Probed provider*' })
    if ($probesDuring.Count -gt 0) { $problems.Add("$($probesDuring.Count) probes while Local-only: $($probesDuring[0].message)") }
    $connLines = @(Get-HostLogEntries -Since $onAt.AddSeconds(-1) | Where-Object { $_.message -like 'Connectivity *' } | ForEach-Object { "$($_.ts) $($_.message)" })

    $offStatus = $null
    if (-not $AlreadyOn) {
        Set-PillionLocalOnly -On $false
        $offStatus = Wait-ChatStatus -Until { param($s) ($c = (Get-HostStateFromStatus $s).Connectivity) -and $c -ne 'Local-only' } -TimeoutSec 30 -What 'Local-only off'
        $connLines = @(Get-HostLogEntries -Since $onAt.AddSeconds(-1) | Where-Object { $_.message -like 'Connectivity *' } | ForEach-Object { "$($_.ts) $($_.message)" })
    }
    [ordered]@{
        Status             = ($problems.Count -eq 0) ? 'Pass' : 'Fail'
        Summary            = ("status '{0}'; {1} cloud entr{2} greyed with the Local-only reason; Auto answered under '{3}'; {4} Outbound line(s), all local: {5}; probes during Local-only: {6} (probe lines seen before: {7})" -f $status.Status, $cloud.Count, ($cloud.Count -eq 1 ? 'y' : 'ies'), $reply.Header, $outbound.Count, ($cloudOutbound.Count -eq 0), $probesDuring.Count, $probesBefore)
        problems           = $problems.ToArray()
        method             = $AlreadyOn ? 'Local-only forced in settings.json before launch' : 'privacy.localOnly toggled in settings.json (host file watcher) plus Refresh models; the Privacy page is not driven'
        statusOn           = $status.Status
        statusSequence     = $status.Seen
        statusBarOn        = $bar
        pickerEntries      = $entries
        pickedEntry        = $picked
        replyHeader        = $reply.Header
        reply              = $reply.Reply
        outboundLines      = @($outbound | ForEach-Object { $_.message })
        probesDuringLocalOnly = $probesDuring.Count
        probeLinesBeforeLocalOnly = $probesBefore
        heldSec            = [math]::Round(($offAt - $onAt).TotalSeconds, 1)
        connectivityLog    = $connLines
        statusAfterOff     = $offStatus.Status
        screenshot         = $shot
    }
}

function Test-CloudKeyStored {
    <# Asks cmdkey whether a credential named PillionProgrammer/<id> exists. The key itself is never read. #>
    param([Parameter(Mandatory)][string]$ProviderId)
    $out = & cmdkey.exe "/list:PillionProgrammer/$ProviderId" 2>&1 | Out-String
    return $out -match [regex]::Escape("PillionProgrammer/$ProviderId")
}

function Invoke-CloudChatCheck {
    <# Step h: only with -CloudProviderId and a stored key; otherwise Pending. Picks the provider's model, sends the prompt, records the header and Outbound line. #>
    param([string]$CloudProviderId, [string]$Prompt = 'Reply with the single word pong')
    if (-not $CloudProviderId) { return [ordered]@{ Status = 'Pending'; Summary = 'pending: no cloud key stored (run again with -CloudProviderId <id> after storing a key)' } }
    if (-not (Test-CloudKeyStored -ProviderId $CloudProviderId)) { return [ordered]@{ Status = 'Pending'; Summary = "pending: no cloud key stored for PillionProgrammer/$CloudProviderId" } }
    $provider = Get-PillionProviderSettings -Id $CloudProviderId
    if (-not $provider) { return [ordered]@{ Status = 'Pending'; Summary = "pending: provider '$CloudProviderId' is not in settings.json (add it on the Providers and keys page)" } }
    $name = if ($provider['displayName']) { $provider['displayName'] } else { $CloudProviderId }
    Clear-ChatConversation
    $entries = @(Get-ChatPickerEntries)
    $entry = $entries | Where-Object { $_.Group -eq 'Cloud' -and $_.IsEnabled -and $_.Name -like "* ($name), cloud model*" } | Select-Object -First 1
    if (-not $entry) { return [ordered]@{ Status = 'Fail'; Summary = "no enabled cloud model of '$name' in the picker"; pickerEntries = $entries } }
    $picked = Select-ChatModel -NamePrefix ($entry.Name -replace ', cloud model.*$', '')
    $since = [DateTimeOffset]::Now.AddSeconds(-1)
    $reply = Send-ChatMessage -Text $Prompt
    $line = @(Get-OutboundEntries -Since $since) | Select-Object -Last 1
    $shot = Save-ExpScreenshot -Name 'cloud-chat'
    $lineText = if ($line) { $line.message } else { '(none found)' }
    $ok = $reply.Finished -and $reply.Header -like "* via $name, cloud" -and $line -and $line.state.Locality -eq 'cloud' -and $line.state.Outcome -eq 'Succeeded'
    [ordered]@{
        Status       = $ok ? 'Pass' : 'Fail'
        Summary      = "header '$($reply.Header)'; reply '$($reply.Reply)'; outbound '$lineText'"
        pickedEntry  = $picked
        header       = $reply.Header
        reply        = $reply.Reply
        notes        = $reply.Notes
        outboundLine = $lineText
        redactions   = $(if ($line) { $line.state.Redactions } else { $null })
        screenshot   = $shot
    }
}

function Invoke-Gate0LiveChecks {
    <#
    Steps d to h against a freshly launched Experimental Instance. The caller owns settings backup and restore and
    closing the instance. Returns the step results.
    #>
    param(
        [Parameter(Mandatory)]$VisualStudio,
        [Parameter(Mandatory)][string]$Folder,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$ProviderName,
        [string]$ExpectConnectivity,
        [string]$CloudProviderId,
        [switch]$LocalOnlyForced,
        [switch]$SkipCloud,
        [int]$LocalOnlyHoldSec = 65
    )
    $steps = [System.Collections.Generic.List[object]]::new()
    $launch = Invoke-Gate0Step -Name 'd. Launch Experimental Instance' -Body { Invoke-ExpLaunchCheck -VisualStudio $VisualStudio -Folder $Folder -ExpectConnectivity $ExpectConnectivity }
    $steps.Add($launch)
    if ($launch.status -ne 'Pass') { return $steps.ToArray() }
    $steps.Add((Invoke-Gate0Step -Name 'e. Local chat check' -Body { Invoke-LocalChatCheck -Model $Model -ProviderName $ProviderName }))
    $steps.Add((Invoke-Gate0Step -Name 'f. Host-kill check' -Body { Invoke-HostKillCheck -Model $Model -ProviderName $ProviderName }))
    if ($ExpectConnectivity) {
        $steps.Add((Invoke-Gate0Step -Name "Connectivity reads $ExpectConnectivity after the restarts" -Body {
                    $s = Wait-ChatStatus -Until { param($t) (Get-HostStateFromStatus $t).Connectivity -eq $ExpectConnectivity } -TimeoutSec 90 -What $ExpectConnectivity
                    [ordered]@{ Summary = "status '$($s.Status)' after $($s.WaitMs) ms"; status = $s.Status; statusBar = Get-ExpStatusBarText }
                }))
    }
    $steps.Add((Invoke-Gate0Step -Name 'g. Local-only check' -Body { Invoke-LocalOnlyCheck -Model $Model -ProviderName $ProviderName -AlreadyOn:$LocalOnlyForced -HoldSec $LocalOnlyHoldSec }))
    if (-not $SkipCloud) {
        $steps.Add((Invoke-Gate0Step -Name 'h. Cloud chat check' -Body { Invoke-CloudChatCheck -CloudProviderId $CloudProviderId }))
    }
    return $steps.ToArray()
}

#endregion

Export-ModuleMember -Function *-*
