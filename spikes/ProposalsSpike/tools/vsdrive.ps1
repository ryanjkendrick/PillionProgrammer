# Helper for driving the VS Experimental Instance. Dot-source, then call functions.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'VsDrive.Native').Type) {
Add-Type -Namespace VsDrive -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
'@
}
[void][VsDrive.Native]::SetProcessDPIAware()

# Set $env:SPIKE_WORK to a scratch folder holding Scratch\Scratch.sln, Scratch\Program.cs and Program.cs.orig (outside the repo).
if (-not $env:SPIKE_WORK) { $env:SPIKE_WORK = Join-Path $env:TEMP "ProposalsSpikeWork" }
if (-not $env:SPIKE_DEVENV) { $env:SPIKE_DEVENV = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" }
$global:ShotDir = Join-Path $env:SPIKE_WORK "shots"
New-Item -ItemType Directory -Force $global:ShotDir | Out-Null

function Get-ExpProcess {
    Get-CimInstance Win32_Process -Filter "name='devenv.exe'" | Where-Object { $_.CommandLine -match 'rootsuffix\s+Exp' } | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Select-Object -First 1
}

function Get-ExpWindow {
    $p = Get-ExpProcess
    if (-not $p) { throw "Experimental instance not running" }
    $p.Refresh()
    return $p.MainWindowHandle
}

function Get-ForegroundInfo {
    $h = [VsDrive.Native]::GetForegroundWindow()
    $fpid = 0; [void][VsDrive.Native]::GetWindowThreadProcessId($h, [ref]$fpid)
    $sb = New-Object System.Text.StringBuilder 512; [void][VsDrive.Native]::GetWindowText($h, $sb, 512)
    [pscustomobject]@{ Handle = $h; Pid = $fpid; Title = $sb.ToString() }
}

function Focus-Exp {
    $p = Get-ExpProcess
    $h = Get-ExpWindow
    $fg = [VsDrive.Native]::GetForegroundWindow()
    $fpid = 0; $ft = [VsDrive.Native]::GetWindowThreadProcessId($fg, [ref]$fpid)
    $me = [VsDrive.Native]::GetCurrentThreadId()
    [void][VsDrive.Native]::AttachThreadInput($me, $ft, $true)
    # ALT tap releases the foreground lock
    [VsDrive.Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [VsDrive.Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][VsDrive.Native]::ShowWindow($h, 9)
    [void][VsDrive.Native]::BringWindowToTop($h)
    [void][VsDrive.Native]::SetForegroundWindow($h)
    [void][VsDrive.Native]::AttachThreadInput($me, $ft, $false)
    Start-Sleep -Milliseconds 400
    $info = Get-ForegroundInfo
    return ($info.Pid -eq $p.Id)
}

function Send-Exp([string]$keys, [int]$delayMs = 150) {
    $p = Get-ExpProcess
    $info = Get-ForegroundInfo
    if ($info.Pid -ne $p.Id) {
        if (-not (Focus-Exp)) { throw "Exp instance is not the foreground window (foreground: pid $($info.Pid) '$($info.Title)'); refusing to send keys" }
    }
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds $delayMs
}

# Type text one key at a time (lets the editor raise TypeChar per character)
function Type-Exp([string]$text, [int]$perKeyMs = 60) {
    foreach ($ch in $text.ToCharArray()) {
        $k = [string]$ch
        if ('+^%~(){}[]'.Contains($k)) { $k = '{' + $k + '}' }
        Send-Exp $k $perKeyMs
    }
}

function Shot-Exp([string]$name, [switch]$Full) {
    $h = Get-ExpWindow
    $r = New-Object VsDrive.Native+RECT
    [void][VsDrive.Native]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $hgt = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $path = Join-Path $global:ShotDir ($name + ".png")
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $path
}

# Crop a region (window-relative) of a saved screenshot, optionally scaled up
function Crop-Shot([string]$name, [int]$x, [int]$y, [int]$w, [int]$h, [double]$scale = 1.0) {
    $src = [System.Drawing.Image]::FromFile((Join-Path $global:ShotDir ($name + ".png")))
    $dw = [int]($w * $scale); $dh = [int]($h * $scale)
    $bmp = New-Object System.Drawing.Bitmap $dw, $dh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $dw, $dh), (New-Object System.Drawing.Rectangle $x, $y, $w, $h), 'Pixel')
    $out = Join-Path $global:ShotDir ($name + "-crop.png")
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $src.Dispose()
    return $out
}

if (-not ([System.Management.Automation.PSTypeName]'VsDrive.Rot').Type) {
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace VsDrive {
public static class Rot {
  [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int r, out IRunningObjectTable p);
  [DllImport("ole32.dll")] static extern int CreateBindCtx(int r, out IBindCtx p);
  public static object Get(string name) {
    IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
    IEnumMoniker e; rot.EnumRunning(out e);
    IMoniker[] m = new IMoniker[1]; IntPtr f = IntPtr.Zero;
    while (e.Next(1, m, f) == 0) {
      IBindCtx ctx; CreateBindCtx(0, out ctx); string n; m[0].GetDisplayName(ctx, null, out n);
      if (n == name) { object o; rot.GetObject(m[0], out o); return o; }
    }
    return null;
  }
  public static string[] Names() {
    var list = new System.Collections.Generic.List<string>();
    IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
    IEnumMoniker e; rot.EnumRunning(out e);
    IMoniker[] m = new IMoniker[1]; IntPtr f = IntPtr.Zero;
    while (e.Next(1, m, f) == 0) { IBindCtx ctx; CreateBindCtx(0, out ctx); string n; m[0].GetDisplayName(ctx, null, out n); list.Add(n); }
    return list.ToArray();
  }
}}
"@
}

function Get-ExpDte {
    $p = Get-ExpProcess
    $name = [VsDrive.Rot]::Names() | Where-Object { $_ -match "^!VisualStudio\.DTE\.[0-9.]+:$($p.Id)$" } | Select-Object -First 1
    if (-not $name) { throw "DTE for pid $($p.Id) not in ROT" }
    return [VsDrive.Rot]::Get($name)
}

# Put the caret at end of a 1-based line in the active document
function Goto-LineEnd([int]$line) {
    $dte = Get-ExpDte
    $sel = $dte.ActiveDocument.Selection
    $sel.GotoLine($line, $false)
    $sel.EndOfLine($false)
}

function Get-DocText {
    $dte = Get-ExpDte
    $td = $dte.ActiveDocument.Object("TextDocument")
    $ep = $td.StartPoint.CreateEditPoint()
    return $ep.GetText($td.EndPoint)
}

if (-not ([System.Management.Automation.PSTypeName]'VsDrive.Mouse').Type) {
Add-Type -Namespace VsDrive -Name Mouse -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
"@
}
# Click at window-relative physical pixel coordinates
function Click-Exp([int]$x, [int]$y) {
    $p = Get-ExpProcess
    $info = Get-ForegroundInfo
    if ($info.Pid -ne $p.Id) { if (-not (Focus-Exp)) { throw "Exp not foreground" } }
    $h = Get-ExpWindow
    $r = New-Object VsDrive.Native+RECT
    [void][VsDrive.Native]::GetWindowRect($h, [ref]$r)
    [void][VsDrive.Mouse]::SetCursorPos($r.L + $x, $r.T + $y)
    Start-Sleep -Milliseconds 100
    [VsDrive.Mouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [VsDrive.Mouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

# Insert a new line with given text after 1-based line N (no smart indent), via the DTE (does not raise TypeChar)
function Insert-LineAfter([int]$line, [string]$text) {
    $dte = Get-ExpDte
    $td = $dte.ActiveDocument.Object("TextDocument")
    $ep = $td.StartPoint.CreateEditPoint()
    $ep.MoveToLineAndOffset($line, 1)
    $ep.EndOfLine()
    $ep.Insert("`r`n" + $text)
}
function Set-Caret([int]$line, [int]$col) {
    $dte = Get-ExpDte
    $dte.ActiveDocument.Selection.MoveToLineAndOffset($line, $col, $false)
}
function Log-Since([int]$n0) { Get-Content (Join-Path $env:TEMP "ProposalsSpike.log") | Select-Object -Skip $n0 | Where-Object { $_ -notmatch 'RESULT .* -> null|-> defer' } }
function Log-Count { (Get-Content (Join-Path $env:TEMP "ProposalsSpike.log")).Count }

function Reset-Doc {
    $dte = Get-ExpDte
    $td = $dte.ActiveDocument.Object("TextDocument")
    $ep = $td.StartPoint.CreateEditPoint()
    $ep.Delete($td.EndPoint)
    $ep.Insert([IO.File]::ReadAllText((Join-Path $env:SPIKE_WORK "Program.cs.orig")))
}
function Start-Exp {
    $sd = $env:SPIKE_WORK
    Start-Process $env:SPIKE_DEVENV -ArgumentList @('/rootsuffix','Exp',"`"$sd\Scratch\Scratch.sln`"",'/Command',"`"File.OpenFile $sd\Scratch\Program.cs`"")
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep 2; $p = Get-ExpProcess; if ($p) { $p.Refresh(); if ($p.MainWindowTitle -match 'Program.cs') { break } } }
    Start-Sleep 8
}
function Stop-Exp { $dte = Get-ExpDte; $dte.Documents.CloseAll(2); $dte.Quit(); Start-Sleep 6 }
