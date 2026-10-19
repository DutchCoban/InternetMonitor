<#
.SYNOPSIS
  Starts the app over a given data directory, opens a page, optionally clicks and scrolls, and
  saves the window as a PNG. One call per screenshot; Make-Screenshots.ps1 makes the calls.

.NOTES
  Clicks are given in window-image coordinates (the window is placed at 40,40 on the primary
  screen) and each is sent twice, 700 ms apart: a single synthetic click is occasionally lost
  while the page is still laying out, and two clicks on a chip, a row or a tab are idempotent.
  A click that is not - a button that starts something - takes a third field: "x,y,1" sends it once.
  Scrolling a page is done by dragging its scrollbar thumb (-Drag): mouse-wheel notches go to the
  focused control, which is not always the page.
  The app is single-instance per session: close any running Internet Monitor first.
#>
param(
  [Parameter(Mandatory)] [string]$Exe,
  [Parameter(Mandatory)] [string]$Data,
  [Parameter(Mandatory)] [string]$Out,
  [string]$Page = "status",
  [int]$Wait = 12,
  [int]$W = 1164,
  [int]$H = 852,
  [string]$Clicks = "",
  [int]$AfterClickWait = 1500,
  [int]$Notches = 0,
  [int]$Shots = 1,
  [string]$Drag = "",        # "x1,y1,x2,y2" in window-image coordinates: drag the left button, e.g. a scrollbar thumb

  [string]$ExtraArgs = ""
)
$ErrorActionPreference = "Stop"
$env:INTERNETMONITOR_DATA_DIR = $Data
$argList = @("--show", "--page", $Page)
if ($ExtraArgs -ne "") { $argList += $ExtraArgs.Split(" ") }
$p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
Start-Sleep -Seconds $Wait
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Drawing;
public static class ImShot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
  public struct RECT { public int L, T, R, B; }
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new Bitmap(r.R - r.L, r.B - r.T)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
      bmp.Save(path);
    }
  }
  public static void Prepare(IntPtr h, int w, int hh) { ShowWindow(h, 9); MoveWindow(h, 40, 40, w, hh, true); SetForegroundWindow(h); SetCursorPos(40 + (w / 2), 40 + (hh / 2)); System.Threading.Thread.Sleep(1500); }
  // Drags with the left button held, in small steps so the scrollbar thumb follows: scrolling a
  // page this way does not depend on which control has the keyboard focus.
  public static void Drag(int x1, int y1, int x2, int y2) {
    SetCursorPos(x1, y1); System.Threading.Thread.Sleep(400); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(200);
    for (int i = 1; i <= 20; i++) { SetCursorPos(x1 + ((x2 - x1) * i / 20), y1 + ((y2 - y1) * i / 20)); System.Threading.Thread.Sleep(30); }
    System.Threading.Thread.Sleep(200); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(800);
  }
  public static void Click(int x, int y, int wait, int times) {
    for (int i = 0; i < times; i++) { SetCursorPos(x, y); System.Threading.Thread.Sleep(500); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(700); }
    System.Threading.Thread.Sleep(wait);
  }
  public static void Park(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(400); }
  public static void Wheel(int notches) { for (int i = 0; i < notches; i++) { mouse_event(0x0800, 0, 0, -120, UIntPtr.Zero); System.Threading.Thread.Sleep(40); } System.Threading.Thread.Sleep(700); }
}
"@ -ReferencedAssemblies System.Drawing
$h = [IntPtr]::Zero
for ($t = 0; $t -lt 40 -and $h -eq [IntPtr]::Zero; $t++) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.HasExited) { throw "The app exited before showing a window - is another Internet Monitor running?" }; $h = $p.MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { Stop-Process -Id $p.Id -Force; throw "No main window after 20 s" }
try {
  [ImShot]::Prepare($h, $W, $H)
  if ($Notches -gt 0) { [ImShot]::Wheel($Notches) }
  if ($Drag -ne "") { $d = $Drag.Split(","); [ImShot]::Drag(40 + [int]$d[0], 40 + [int]$d[1], 40 + [int]$d[2], 40 + [int]$d[3]) }
  if ($Clicks -ne "") {
    foreach ($c in $Clicks.Split(";")) { $xy = $c.Split(","); $times = if ($xy.Length -gt 2) { [int]$xy[2] } else { 2 }; [ImShot]::Click(40 + [int]$xy[0], 40 + [int]$xy[1], $AfterClickWait, $times) }
  }
  [ImShot]::Park(40 + $W - 30, 40 + $H - 30)
  $base = [System.IO.Path]::ChangeExtension($Out, $null).TrimEnd('.')
  [ImShot]::Shot($h, $Out)
  for ($i = 2; $i -le $Shots; $i++) {
    [ImShot]::Park(40 + [int]($W / 2), 40 + [int]($H / 2)); [ImShot]::Wheel($Notches); [ImShot]::Park(40 + $W - 30, 40 + $H - 30)
    [ImShot]::Shot($h, "$base-$i.png")
  }
  Write-Host "  $([System.IO.Path]::GetFileName($Out))"
}
finally {
  Stop-Process -Id $p.Id -Force
}
