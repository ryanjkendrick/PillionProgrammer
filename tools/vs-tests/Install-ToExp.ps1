#Requires -Version 7.2
<#
.SYNOPSIS
    Installs the built Pillion VSIX into the Experimental Instance of one Visual Studio, ready for the integration tests.

.DESCRIPTION
    Uses that Visual Studio's own VSIXInstaller.exe (/quiet /rootSuffix:Exp): uninstalls an earlier copy of the
    extension, installs the .vsix, then runs devenv /rootsuffix Exp /updateconfiguration so the instance rebuilds its
    package cache (Visual Studio 2022 17.14 otherwise kept its cached configuration and never loaded the package; see
    docs/gate-0-report.md). Checks that the extension folder, with Host\Pillion.Host.exe, is in the hive afterwards.

    Refuses to run while an Experimental Instance of that Visual Studio is open. Touches nothing outside that
    instance's hive; other Visual Studio instances and their Experimental Instances are left alone.

.PARAMETER DevEnvPath
    The devenv.exe of the Visual Studio to install into. Default: the newest installed Visual Studio.

.PARAMETER VsixPath
    The .vsix to install. Default: src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix.

.PARAMETER Uninstall
    Remove the extension from the Experimental Instance instead of installing it.

.EXAMPLE
    pwsh tools/vs-tests/Install-ToExp.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
#>
[CmdletBinding()]
param(
    [string]$DevEnvPath,
    [string]$VsixPath,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VsTests.psm1') -Force
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $VsixPath) { $VsixPath = Join-Path $repoRoot 'src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix' }

$vs = Find-TargetVisualStudio -DevEnvPath $DevEnvPath
Write-Host "Target: $($vs.DisplayName) ($($vs.DevEnv)); Experimental Instance hive $($vs.ExpHive)"
if (-not (Test-Path -LiteralPath $vs.VsixInstaller)) { throw "VSIXInstaller.exe not found at $($vs.VsixInstaller)." }
$running = Get-ExpInstanceProcess -VisualStudio $vs
if ($running.Count -gt 0) { throw "An Experimental Instance of $($vs.DisplayName) is running (pid $($running.ProcessId -join ', ')). Close it first." }

function Invoke-Tool {
    param([string]$FilePath, [string[]]$Arguments, [string]$What)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -Wait -NoNewWindow
    Write-Host ("{0}: exit {1} in {2:0.0} s" -f $What, $p.ExitCode, $sw.Elapsed.TotalSeconds)
    return $p.ExitCode
}

$extensionId = $ExtensionId
if (Test-Path -LiteralPath $VsixPath) { $extensionId = (Get-VsixIdentity -VsixPath $VsixPath).Id }

# Exit code 0 when an earlier copy was removed; non-zero when there was none, which is fine.
[void](Invoke-Tool $vs.VsixInstaller @('/quiet', '/rootSuffix:Exp', "/uninstall:$extensionId") "Uninstall $extensionId")

if (-not $Uninstall) {
    if (-not (Test-Path -LiteralPath $VsixPath)) { throw "No VSIX at $VsixPath. Build the full solution first (see README.md)." }
    $VsixPath = (Resolve-Path -LiteralPath $VsixPath).Path
    $identity = Get-VsixIdentity -VsixPath $VsixPath
    Write-Host "Installing $($identity.Id) $($identity.Version) from $VsixPath"
    $code = Invoke-Tool $vs.VsixInstaller @('/quiet', '/rootSuffix:Exp', "`"$VsixPath`"") 'Install'
    if ($code -ne 0) { throw "VSIXInstaller failed with exit code $code; see the newest %TEMP%\dd_VSIXInstaller_*.log." }
}

$code = Invoke-Tool $vs.DevEnv @('/rootsuffix', 'Exp', '/updateconfiguration') 'devenv /rootsuffix Exp /updateconfiguration'
if ($code -ne 0) { throw "devenv /updateconfiguration failed with exit code $code." }

$deployed = Find-DeployedPillion -ExpHive $vs.ExpHive
if ($Uninstall) {
    # Visual Studio deletes an uninstalled extension's folder on its next start, so the folder may still be there.
    if ($deployed) { Write-Host "Uninstalled; the folder $($deployed.Directory) is removed when the instance next starts." }
    else { Write-Host 'Pillion removed from the Experimental Instance.' }
}
else {
    if (-not $deployed) { throw "Pillion.Host.exe not found under $($vs.ExpHive)\Extensions after installing." }
    Write-Host "Deployed to $($deployed.Directory)"
}
