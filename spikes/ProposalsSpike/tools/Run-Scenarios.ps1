<#
.SYNOPSIS
  Drives the Proposals spike scenarios in a Visual Studio Experimental Instance and prints the spike log per scenario.

.DESCRIPTION
  Prerequisites: the spike VSIX is deployed to the Exp hive of the VS you are testing (see FINDINGS.md checklist).
  Keep hands off the keyboard and mouse while it runs: keys are sent with SendKeys to the foreground window
  (the script refuses to type if the foreground window is not the Experimental Instance).
  Screenshots go to $env:SPIKE_WORK\shots (default %TEMP%\ProposalsSpikeWork\shots), outside the repo.

.PARAMETER DevEnv
  devenv.exe to launch, e.g. "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe".

.PARAMETER EditorClick
  Window-relative pixel (x,y) inside the editor, right of line 10's text, used once to give the editor keyboard focus.
#>
param(
    [string]$DevEnv = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
    [int[]]$EditorClick = @(1150, 302),
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$env:SPIKE_DEVENV = $DevEnv
if (-not $env:SPIKE_WORK) { $env:SPIKE_WORK = Join-Path $env:TEMP "ProposalsSpikeWork" }
$work = $env:SPIKE_WORK

# 1. Throwaway console project outside the repo
if (-not (Test-Path (Join-Path $work "Scratch\Scratch.sln"))) {
    New-Item -ItemType Directory -Force (Join-Path $work "Scratch") | Out-Null
    Push-Location (Join-Path $work "Scratch")
    dotnet new console -n Scratch -o . --framework net8.0 --force | Out-Null
    dotnet new sln -n Scratch --format sln --force | Out-Null
    dotnet sln Scratch.sln add Scratch.csproj | Out-Null
    Pop-Location
}
$lines = @('using System;', '', 'namespace Scratch', '{', '    internal static class Program', '    {', '        private static void Main()', '        {',
    '            Console.WriteLine("scratch");', '            // ANCHOR-1', '            // ANCHOR-2', '            // ANCHOR-3', '            // ANCHOR-4',
    '            // ANCHOR-5', '            // ANCHOR-6', '            // ANCHOR-7', '            // ANCHOR-8', '        }', '    }', '}', '')
[IO.File]::WriteAllText((Join-Path $work "Scratch\Program.cs"), ($lines -join "`r`n"))
[IO.File]::WriteAllText((Join-Path $work "Program.cs.orig"), ($lines -join "`r`n"))

. (Join-Path $PSScriptRoot "vsdrive.ps1")
$log = Join-Path $env:TEMP "ProposalsSpike.log"
Remove-Item $log -ErrorAction SilentlyContinue

if (-not (Get-ExpProcess)) { Start-Exp }
Focus-Exp | Out-Null
Click-Exp $EditorClick[0] $EditorClick[1]

function Show-Scenario([string]$name, [int]$n0) {
    Write-Host "==== $name" -ForegroundColor Cyan
    Log-Since $n0 | Where-Object { $_ -match 'ours=True|TryCreate|note:|ERROR' } | ForEach-Object { Write-Host "  $_" }
}

function Step([string]$name, [scriptblock]$arrange, [string]$accept = '{TAB}') {
    Send-Exp "{ESC}" 300
    Reset-Doc; Start-Sleep 1
    $n0 = Log-Count
    & $arrange
    Start-Sleep 2.5
    Shot-Exp "$name-shown" | Out-Null
    if ($accept) { Send-Exp $accept 1500; Shot-Exp "$name-after" | Out-Null }
    Show-Scenario $name $n0
    $doc = (Get-DocText).Split("`n")
    Write-Host ("  lines 10-14 after: " + (($doc[9..13] | ForEach-Object { $_.TrimEnd("`r") }) -join ' | '))
}

Step "ml"       { Goto-LineEnd 10; Send-Exp "{ENTER}" 400; Type-Exp "//ml" 150 }
Step "esc"      { Goto-LineEnd 10; Send-Exp "{ENTER}" 400; Type-Exp "//noflags" 120 } '{ESC}'
Step "right"    { Insert-LineAfter 10 '            Console.WriteLine();'; Set-Caret 11 31; Start-Sleep -Milliseconds 500; Type-Exp "/*right*/" 120 }
Step "rightml"  { Insert-LineAfter 10 '            Console.WriteLine();'; Set-Caret 11 31; Start-Sleep -Milliseconds 500; Type-Exp "/*rightml*/" 100 }
Step "replace"  { Insert-LineAfter 10 '            Console.WriteLine();'; Set-Caret 11 31; Start-Sleep -Milliseconds 500; Type-Exp "/*replace*/" 100 }
Step "vs"       { Insert-LineAfter 10 '            //vs'; Goto-LineEnd 11; Start-Sleep -Milliseconds 500; Send-Exp "{ENTER}" 100 }
Step "vs0"      { Insert-LineAfter 10 '            //vs0'; Goto-LineEnd 11; Start-Sleep -Milliseconds 500; Send-Exp "{ENTER}" 100 }
Step "ind"      { Insert-LineAfter 10 '            //ind'; Insert-LineAfter 11 '            '; Goto-LineEnd 12; Start-Sleep -Milliseconds 500; Send-Exp "{END}" 300; Send-Exp " " 100 }
Step "two"      { Goto-LineEnd 10; Send-Exp "{ENTER}" 400; Type-Exp "//two" 120; Start-Sleep 2; Send-Exp "%." 1500 } '{ESC}'
Step "isense"   { Insert-LineAfter 10 '            //isense'; Goto-LineEnd 11; Send-Exp "{ENTER}" 400; Type-Exp "Console.WriteL" 300 } '{ESC}'

if (-not $KeepOpen) { Send-Exp "{ESC}" 300; Stop-Exp }
Write-Host "Full log: $log ; screenshots: $global:ShotDir"
