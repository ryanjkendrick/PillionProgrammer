#Requires -Version 7.2
<#
.SYNOPSIS
    Shared helpers for tools/vs-tests: find the Visual Studio under test, back up and restore Pillion's settings.json,
    and find Experimental Instances and Pillion Host processes.
#>

Set-StrictMode -Version 3.0

$script:PillionDataDir = Join-Path $env:LOCALAPPDATA 'PillionProgrammer'
$script:SettingsPath = Join-Path $script:PillionDataDir 'settings.json'
$script:MinimumVersion = [version]'17.14'
$script:ExtensionId = 'PillionProgrammer.e4d0d8fe-694c-4b1f-bcb1-31b346cda51e'

function Get-VsWherePath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw "vswhere.exe not found at $vswhere; is Visual Studio installed?" }
    return $vswhere
}

function Find-TargetVisualStudio {
    <#
    .SYNOPSIS
        Describes the Visual Studio whose Experimental Instance is under test: the one that owns -DevEnvPath, or the
        newest installed one. Throws when it is older than 17.14.
    #>
    param([string]$DevEnvPath)
    $vswhere = Get-VsWherePath
    $all = @(& $vswhere -all -prerelease -products * -format json | ConvertFrom-Json) | Where-Object { $_ }
    if ($DevEnvPath) {
        if (-not (Test-Path -LiteralPath $DevEnvPath -PathType Leaf)) { throw "-DevEnvPath: no file at '$DevEnvPath'." }
        $full = (Resolve-Path -LiteralPath $DevEnvPath).Path
        $vs = $all | Where-Object { $full.StartsWith((Join-Path $_.installationPath 'Common7\IDE\'), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $vs) { throw "-DevEnvPath: '$full' does not belong to any Visual Studio that vswhere knows." }
    }
    else {
        $vs = $all | Where-Object { Test-Path -LiteralPath (Join-Path $_.installationPath 'Common7\IDE\devenv.exe') } |
            Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
        if (-not $vs) { throw 'No Visual Studio with devenv.exe found.' }
    }

    $version = [version]$vs.installationVersion
    if ($version -lt $script:MinimumVersion) { throw "$($vs.displayName) $($vs.catalog.productDisplayVersion) is older than $script:MinimumVersion, the oldest version Pillion supports." }
    $ide = Join-Path $vs.installationPath 'Common7\IDE'
    $major = $version.Major
    $intelliCode = @(& $vswhere -all -prerelease -products * -requires Microsoft.VisualStudio.Component.IntelliCode -format json | ConvertFrom-Json) |
        Where-Object { $_ -and $_.instanceId -eq $vs.instanceId }
    [pscustomobject]@{
        DisplayName      = "$($vs.displayName) $($vs.catalog.productDisplayVersion)"
        Version          = $version
        Major            = $major
        InstanceId       = $vs.instanceId
        Path             = $vs.installationPath
        DevEnv           = Join-Path $ide 'devenv.exe'
        VsixInstaller    = Join-Path $ide 'VSIXInstaller.exe'
        VsTestConsole    = Join-Path $ide 'Extensions\TestPlatform\vstest.console.exe'
        HasIntelliCode   = @($intelliCode).Count -gt 0
        # The framework's names for the version (VisualStudioVersion.VS2022, VisualStudioVersion.VS18); every test's
        # display name ends with one of them in parentheses.
        TestVersionName  = switch ($major) { 17 { 'VS2022' } 18 { 'VS18' } default { "VS$major" } }
        ExpHive          = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$major.0_$($vs.instanceId)Exp"
        ExpRoamingHive   = Join-Path $env:APPDATA "Microsoft\VisualStudio\$major.0_$($vs.instanceId)Exp"
    }
}

function Get-ExpInstanceProcess {
    <# Running devenv.exe processes of this Visual Studio started with /rootsuffix Exp. #>
    param([Parameter(Mandatory)]$VisualStudio)
    @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.Equals($VisualStudio.DevEnv, [StringComparison]::OrdinalIgnoreCase) -and $_.CommandLine -match '/rootsuffix\s+"?Exp\b'
        })
}

function Get-PillionHostProcess {
    <# Running Pillion.Host.exe processes with their parent process id. #>
    @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe'" | Select-Object ProcessId, ParentProcessId, ExecutablePath)
}

function Get-AncestorProcessId {
    <# The process ids above a process, nearest first (parent, grandparent, ...), as far as the processes still exist. #>
    param([Parameter(Mandatory)][int]$ProcessId)
    $ids = [Collections.Generic.List[int]]::new()
    $current = $ProcessId
    for ($depth = 0; $depth -lt 8; $depth++) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$current" -ErrorAction SilentlyContinue
        if (-not $process -or -not $process.ParentProcessId -or $process.ParentProcessId -eq $current) { break }
        $ids.Add([int]$process.ParentProcessId)
        $current = [int]$process.ParentProcessId
    }
    $ids.ToArray()
}

function Get-VsixIdentity {
    <# The Identity element (Id, Version) of a .vsix's extension.vsixmanifest. #>
    param([Parameter(Mandatory)][string]$VsixPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($VsixPath)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('extension.vsixmanifest').Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $xml.PackageManifest.Metadata.Identity
        [pscustomobject]@{ Id = $identity.Id; Version = $identity.Version }
    }
    finally { $zip.Dispose() }
}

function Find-DeployedPillion {
    <# The Pillion extension folder in an Experimental Instance hive (the one holding Host\Pillion.Host.exe), or $null. #>
    param([Parameter(Mandatory)][string]$ExpHive)
    $extensions = Join-Path $ExpHive 'Extensions'
    if (-not (Test-Path -LiteralPath $extensions)) { return $null }
    $exe = Get-ChildItem -LiteralPath $extensions -Recurse -Filter 'Pillion.Host.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { return $null }
    $dir = Split-Path (Split-Path $exe.FullName -Parent) -Parent
    [pscustomobject]@{ Directory = $dir; HostExe = $exe.FullName; PackageDll = Join-Path $dir 'Pillion.VisualStudio.dll' }
}

function Backup-PillionSettings {
    <# Keeps the exact bytes of %LOCALAPPDATA%\PillionProgrammer\settings.json (or that it was absent). #>
    $backup = [ordered]@{ Existed = (Test-Path -LiteralPath $script:SettingsPath); Bytes = $null; Sha256 = $null }
    if ($backup.Existed) {
        $backup.Bytes = [IO.File]::ReadAllBytes($script:SettingsPath)
        $backup.Sha256 = (Get-FileHash -LiteralPath $script:SettingsPath -Algorithm SHA256).Hash
    }
    Write-Host ("settings.json backed up (existed: {0}{1})" -f $backup.Existed, ($backup.Existed ? ", sha256 $($backup.Sha256)" : ''))
    return [pscustomobject]$backup
}

function Restore-PillionSettings {
    <# Restores settings.json byte for byte, or removes the one the run created when there was none. #>
    param([Parameter(Mandatory)]$Backup)
    if ($Backup.Existed) {
        [IO.File]::WriteAllBytes($script:SettingsPath, $Backup.Bytes)
        $hash = (Get-FileHash -LiteralPath $script:SettingsPath -Algorithm SHA256).Hash
        Write-Host "settings.json restored (matches backup: $($hash -eq $Backup.Sha256))"
    }
    elseif (Test-Path -LiteralPath $script:SettingsPath) {
        Remove-Item -LiteralPath $script:SettingsPath -Force
        Write-Host 'settings.json did not exist before the run; removed the one the run created.'
    }
}

Export-ModuleMember -Function Find-TargetVisualStudio, Get-ExpInstanceProcess, Get-PillionHostProcess, Get-AncestorProcessId, Get-VsixIdentity, Find-DeployedPillion, Backup-PillionSettings, Restore-PillionSettings -Variable ExtensionId
