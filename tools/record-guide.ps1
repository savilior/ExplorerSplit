# Records a short MP4 guide of Explorer2's main features.
#
# It starts a separate --standalone Explorer2 over a demo folder tree (with demo Favorites), drives
# it with keys and a few right-clicks (never drags, never copies, moves or deletes; F5 is shown and
# cancelled), shows a caption for each step, and records the
# window with ffmpeg. Your real favorites and folders are not shown or changed, but the navigation
# pane still shows this PC's drives and OneDrive, so review the video before publishing it.
#
# Run it with the screen unlocked and leave the mouse and keyboard alone until it ends (~2 min).
#   pwsh -File tools\record-guide.ps1 [-Out docs\guide.mp4] [-DemoRoot <folder>] [-Exe <Explorer2.exe>]
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\docs\guide.mp4"),
    [string]$DemoRoot = (Join-Path $env:TEMP "Explorer2Guide"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows\Explorer2.exe"),
    [switch]$NoRecord
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Guide {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
"@
[Guide]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null   # per-monitor v2: real pixels

# ---------- demo content (only inside $DemoRoot) ----------
if (Test-Path $DemoRoot) { Remove-Item $DemoRoot -Recurse -Force }
$work = New-Item -ItemType Directory -Force (Join-Path $DemoRoot "Projects")
$backup = New-Item -ItemType Directory -Force (Join-Path $DemoRoot "Backup")
foreach ($d in "Designs", "Invoices", "Photos", "Music") { New-Item -ItemType Directory -Force (Join-Path $work $d) | Out-Null }
foreach ($d in "2025", "2026") { New-Item -ItemType Directory -Force (Join-Path $work "Invoices\$d") | Out-Null }
Set-Content (Join-Path $work "Notes.txt") "Explorer2 guide`r`n`r`nSplit mode, tabs, Favorites and Total Commander keys in one window."
Set-Content (Join-Path $work "Budget.csv") "Item,Cost`r`nLaptop stand,51`r`nCables,12"
Set-Content (Join-Path $work "Plan.md") "# Plan`r`n- Try split mode`r`n- Pin favorites"
Set-Content (Join-Path $work "Readme.txt") "Demo files for the Explorer2 guide."
$favorites = @(
    @{ Path = $work.FullName; Children = @(
        @{ Path = (Join-Path $work "Invoices"); Children = @(
            @{ Path = (Join-Path $work "Invoices\2025") },
            @{ Path = (Join-Path $work "Invoices\2026") }) },
        @{ Path = (Join-Path $work "Designs") }) },
    @{ Path = $backup.FullName },
    @{ Name = "Media"; Children = @(
        @{ Path = (Join-Path $work "Photos") },
        @{ Path = (Join-Path $work "Music") }) }
)
$favFile = Join-Path $DemoRoot "favorites.json"
$favorites | ConvertTo-Json -Depth 8 | Set-Content $favFile -Encoding utf8

# ---------- start a standalone window ----------
$env:EXPLORER2_FAVORITES_FILE = $favFile
$proc = Start-Process $Exe -ArgumentList "--standalone", "`"$($work.FullName)`"" -PassThru
for ($i = 0; $i -lt 50 -and $proc.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 200; $proc.Refresh() }
$h = $proc.MainWindowHandle
if ($h -eq 0) { throw "Explorer2 did not open a window" }
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$W = [Math]::Min(1600, $wa.Width - 80); $H = [Math]::Min(900, $wa.Height - 80)
$X0 = $wa.Left + 40; $Y0 = $wa.Top + 40
[Guide]::MoveWindow($h, $X0, $Y0, $W, $H, $true) | Out-Null
Start-Sleep 2
$scale = [Guide]::GetDpiForWindow($h) / 96.0
function Px([double]$v) { [int][Math]::Round($v * $scale) }

# ---------- caption box (recorded with the window) ----------
$caption = New-Object System.Windows.Forms.Form
$caption.FormBorderStyle = "None"; $caption.TopMost = $true; $caption.ShowInTaskbar = $false
$caption.BackColor = [System.Drawing.Color]::FromArgb(32, 32, 36); $caption.Opacity = 0.92
$caption.StartPosition = "Manual"
$label = New-Object System.Windows.Forms.Label
$label.Dock = "Fill"; $label.ForeColor = [System.Drawing.Color]::White; $label.TextAlign = "MiddleCenter"
$label.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 15)
$caption.Controls.Add($label)
$caption.Size = New-Object System.Drawing.Size((Px 720), (Px 64))
$caption.Location = New-Object System.Drawing.Point(($X0 + ($W - $caption.Width) / 2), ($Y0 + $H - $caption.Height - (Px 70)))
$caption.Show()

function Pump([double]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30 }
}
function Say([string]$text, [double]$hold = 2.2) { $label.Text = $text; $caption.Refresh(); Pump $hold }
function Front { [Guide]::SetForegroundWindow($h) | Out-Null; Pump 0.3 }
$vk = @{ Esc = 0x1B; Enter = 0x0D; Tab = 0x09; Space = 0x20; Down = 0x28; Up = 0x26; Right = 0x27; Left = 0x25
         F3 = 0x72; F5 = 0x74; F7 = 0x76; Ctrl = 0x11; Shift = 0x10; Alt = 0x12; S = 0x53; E = 0x45; F = 0x46; Home = 0x24 }
function Key([string[]]$chord, [double]$after = 0.6) {
    Front
    foreach ($k in $chord) { [Guide]::keybd_event([byte]$vk[$k], 0, 0, [IntPtr]::Zero) }
    [array]::Reverse($chord)
    foreach ($k in $chord) { [Guide]::keybd_event([byte]$vk[$k], 0, 2, [IntPtr]::Zero) }
    Pump $after
}
function TypeText([string]$text) { Front; [System.Windows.Forms.SendKeys]::SendWait($text); Pump 0.6 }
function MoveTo([int]$x, [int]$y) {
    # Glide the cursor so the viewer can follow it
    $p = [System.Windows.Forms.Cursor]::Position
    for ($i = 1; $i -le 15; $i++) {
        [Guide]::SetCursorPos([int]($p.X + ($x - $p.X) * $i / 15), [int]($p.Y + ($y - $p.Y) * $i / 15)) | Out-Null
        Pump 0.02
    }
}
function RightClick([int]$x, [int]$y) {
    Front; MoveTo $x $y; Pump 0.2
    [Guide]::mouse_event(0x8, 0, 0, 0, [IntPtr]::Zero); [Guide]::mouse_event(0x10, 0, 0, 0, [IntPtr]::Zero); Pump 1.2
}

# Window-relative points (logical pixels from the window's top-left, scaled)
$client = Px 7   # invisible resize border
function At([double]$x, [double]$y) { @(($X0 + $client + (Px $x)), ($Y0 + (Px $y))) }

# ---------- record ----------
$ffmpeg = $null
if (-not $NoRecord) {
    $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
    if (-not $ff) { throw "ffmpeg not found on PATH (winget install Gyan.FFmpeg)" }
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo $ff
    $psi.Arguments = "-y -f gdigrab -framerate 30 -draw_mouse 1 -offset_x $X0 -offset_y $Y0 -video_size ${W}x${H} -i desktop " +
                     "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -vf scale=trunc(iw/2)*2:trunc(ih/2)*2 `"$Out`""
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.CreateNoWindow = $true
    $ffmpeg = [System.Diagnostics.Process]::Start($psi)
    Pump 1.5
}

try {
    Say "Explorer2: File Explorer with tabs, split mode and Total Commander keys" 3

    Say "Esc clears the selection"
    Key Down; Key @("Shift", "Down"); Key @("Shift", "Down") 0.8
    Key Esc 1.2

    Say "Split mode (Ctrl+Shift+S): two folders side by side"
    Key @("Ctrl", "Shift", "S") 2
    Say "Tab switches between the panes"
    Key Tab 1; Key Tab 1

    Say "Space marks files, F5 copies them to the other pane"
    Key Home; for ($i = 0; $i -lt 6; $i++) { Key Down 0.15 }
    Key Space 0.4; Key Space 0.8
    # Shows the copy dialog, then cancels it: the guide never copies or moves anything
    Key F5 2.5
    Key Esc 1

    Say "F3: quick viewer"
    Key Home; for ($i = 0; $i -lt 7; $i++) { Key Down 0.15 }
    Key F3 2.5; Key Esc 1

    Say "Right-click a tab for the tab menu"
    $p = At 100 21; RightClick $p[0] $p[1]
    Pump 1.5; Key Esc 0.8

    Say "Right-click the address bar"
    $p = At 400 62; RightClick $p[0] $p[1]
    Pump 1.5; Key Esc 0.8

    Say "Favorites: links, with nested links that open as you point at them"
    Key @("Ctrl", "Shift", "E") 1
    $p = At 110 150; MoveTo $p[0] $p[1]; Pump 2.5
    $p = At 240 185; MoveTo $p[0] $p[1]; Pump 2.5
    Key Esc 0.5

    Say "Search the current folder (Ctrl+F)"
    Key @("Ctrl", "F") 0.6; TypeText "plan"; Pump 2; Key Esc 1

    Say "Rest on Maximize for Snap layouts"
    $p = @(($X0 + $W - (Px 7) - (Px 69)), ($Y0 + (Px 20))); MoveTo $p[0] $p[1]; Pump 3
    $p = At 600 400; MoveTo $p[0] $p[1]

    Say "Explorer2: one window, two panes" 2.5
}
finally {
    if ($ffmpeg) { $ffmpeg.StandardInput.Write("q"); $ffmpeg.WaitForExit(15000) | Out-Null }
    $caption.Close()
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $proc.HasExited) { $proc.Kill() } }
    Remove-Item Env:EXPLORER2_FAVORITES_FILE -ErrorAction SilentlyContinue
}
if (-not $NoRecord) { "Saved $Out" }
