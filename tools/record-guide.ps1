# Records a short MP4 guide of Explorer2, in four chapters:
#   1. It looks and works like File Explorer, with faster selection (Space, Num *, Esc)
#   2. The environment: Preferences, a live Dark theme, the Explorer2 button on File Explorer
#   3. Favorites: drag items in, nest links, fly-outs, rename, groups
#   4. Two panes: Tab between them, function keys (F5 copies to the other pane)
#
# It runs a separate Explorer2 in demo mode (EXPLORER2_DATA_DIR): its own tray, settings,
# favorites and history in the demo folder, next to your installed Explorer2 without touching it,
# and it never changes the registry. All files it creates, copies or drops are inside -DemoRoot.
# Drags start in the demo file list and are dropped only after checking that the point under the
# cursor is a Favorites list (its window text); otherwise the drag is cancelled with Esc. Every key
# and click first checks that the window in front belongs to the demo Explorer2, and stops if not.
#
# The navigation pane still shows this PC's drives and OneDrive, and paths show your user name:
# review the video before publishing it.
#
# Run it with the screen unlocked and leave the mouse and keyboard alone until it ends (~3 min).
#   pwsh -File tools\record-guide.ps1 [-Out docs\guide.mp4] [-DemoRoot <folder>] [-Exe <Explorer2.exe>]
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\docs\guide.mp4"),
    [string]$DemoRoot = (Join-Path $env:TEMP "Explorer2Guide"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows\Explorer2.exe"),
    [switch]$NoRecord,
    [switch]$Diag   # only open and place the window, print where it is, and close it
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Guide {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  public static uint ForegroundPid() { return PidOf(GetForegroundWindow()); }
  public static IntPtr WindowAt(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  public static string TextOf(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
  // The process's largest visible top-level window
  public static IntPtr MainWindowOf(uint pid, string titlePart) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows((h, l) => {
      RECT r;
      if (PidOf(h) == pid && IsWindowVisible(h) && GetWindowRect(h, out r) &&
          (titlePart == null || TextOf(h).Contains(titlePart))) {
        long area = (long)(r.R - r.L) * (r.B - r.T);
        if (area > bestArea) { bestArea = area; best = h; }
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
"@
[Guide]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null   # per-monitor v2: real pixels
$UIA = [System.Windows.Automation.AutomationElement]

# ---------- demo content (only inside $DemoRoot) ----------
if (Test-Path $DemoRoot) { Remove-Item $DemoRoot -Recurse -Force }
$work = New-Item -ItemType Directory -Force (Join-Path $DemoRoot "Projects")
$backup = New-Item -ItemType Directory -Force (Join-Path $DemoRoot "Backup")
$data = New-Item -ItemType Directory -Force (Join-Path $DemoRoot "data")
foreach ($d in "Designs", "Invoices", "Music", "Photos") { New-Item -ItemType Directory -Force (Join-Path $work $d) | Out-Null }
foreach ($d in "2025", "2026") { New-Item -ItemType Directory -Force (Join-Path $work "Invoices\$d") | Out-Null }
Set-Content (Join-Path $work "Budget.csv") "Item,Cost`r`nLaptop stand,51`r`nCables,12"
Set-Content (Join-Path $work "Notes.txt") "Explorer2 guide`r`n`r`nSplit mode, tabs, Favorites and Total Commander keys in one window."
Set-Content (Join-Path $work "Plan.md") "# Plan`r`n- Try split mode`r`n- Pin favorites"
Set-Content (Join-Path $work "Readme.txt") "Demo files for the Explorer2 guide."
Set-Content (Join-Path $work "Invoices\2026\Invoice 001.txt") "Invoice 001"
@(
    @{ Path = $work.FullName; Children = @(
        @{ Path = (Join-Path $work "Invoices"); Children = @(
            @{ Path = (Join-Path $work "Invoices\2025") },
            @{ Path = (Join-Path $work "Invoices\2026") }) },
        @{ Path = (Join-Path $work "Designs") }) },
    @{ Name = "Media"; Children = @(
        @{ Path = (Join-Path $work "Photos") }) }
) | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data "favorites.json") -Encoding utf8
# The marker says the default folders were already handled, so the list is used as written
Set-Content (Join-Path $data "favorites.json.defaults") ""
@{
    Theme = "Light"; ShowTitleBarButton = $true; FavoritesSection = $true; DefaultStartInSplitMode = $false
    DefaultRightPaneMode = "Custom"; CustomRightPanePath = $backup.FullName
} | ConvertTo-Json | Set-Content (Join-Path $data "settings.json") -Encoding utf8

# ---------- start the demo Explorer2 ----------
$env:EXPLORER2_DATA_DIR = $data.FullName
$proc = Start-Process $Exe -ArgumentList "`"$($work.FullName)`"" -PassThru
Remove-Item Env:EXPLORER2_DATA_DIR
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
# (PowerShell names ignore case: the size must not be called $H, which is the window handle $h)
$WinW = [Math]::Min(1600, $wa.Width - 80); $WinH = [Math]::Min(900, $wa.Height - 80)
$X0 = $wa.Left + 40; $Y0 = $wa.Top + 40

function Pump([double]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30 }
}

# Finds the demo window (again after a theme change reopens it) and puts it in place
function Place {
    $script:h = [IntPtr]::Zero
    for ($i = 0; $i -lt 50 -and $script:h -eq [IntPtr]::Zero; $i++) { Pump 0.2; $script:h = [Guide]::MainWindowOf([uint32]$proc.Id, $null) }
    if ($script:h -eq [IntPtr]::Zero) { throw "Explorer2 did not open a window" }
    Pump 1
    $script:h = [Guide]::MainWindowOf([uint32]$proc.Id, $null)
    for ($i = 0; $i -lt 5; $i++) {
        [Guide]::MoveWindow($script:h, $X0, $Y0, $WinW, $WinH, $true) | Out-Null
        Pump 0.6
        $r = New-Object Guide+RECT
        [Guide]::GetWindowRect($script:h, [ref]$r) | Out-Null
        if ($r.L -eq $X0 -and $r.T -eq $Y0 -and ($r.R - $r.L) -eq $WinW) { break }
    }
}
Place
$scale = [Guide]::GetDpiForWindow($h) / 96.0
function Px([double]$v) { [int][Math]::Round($v * $scale) }
$r = New-Object Guide+RECT
[Guide]::GetWindowRect($h, [ref]$r) | Out-Null
Write-Host "window $h at $($r.L),$($r.T),$($r.R),$($r.B); wanted $X0,$Y0 ${WinW}x${WinH}; dpi scale $scale"
if ($Diag) { $proc.Kill(); return }

# ---------- caption box (recorded with the window) ----------
$caption = New-Object System.Windows.Forms.Form
$caption.FormBorderStyle = "None"; $caption.TopMost = $true; $caption.ShowInTaskbar = $false
$caption.BackColor = [System.Drawing.Color]::FromArgb(32, 32, 36); $caption.Opacity = 0.94
$caption.StartPosition = "Manual"
$label = New-Object System.Windows.Forms.Label
$label.Dock = "Fill"; $label.ForeColor = [System.Drawing.Color]::White; $label.TextAlign = "MiddleCenter"
$label.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 14)
$caption.Controls.Add($label)
$caption.Size = New-Object System.Drawing.Size((Px 900), (Px 78))
$caption.Location = New-Object System.Drawing.Point(($X0 + ($WinW - $caption.Width) / 2), ($Y0 + $WinH - $caption.Height - (Px 60)))
$caption.Show()

function Say([string]$text, [double]$hold = 2.5) { $label.Text = $text; $caption.Refresh(); Pump $hold }

$vk = @{ Esc = 0x1B; Enter = 0x0D; Tab = 0x09; Space = 0x20; Down = 0x28; Up = 0x26; Home = 0x24
         F2 = 0x71; F5 = 0x74; Ctrl = 0x11; Shift = 0x10; S = 0x53; A = 0x41; Multiply = 0x6A }

# Keys and clicks must only ever reach the demo Explorer2 (its windows, menus or dialogs)
function Front {
    if ([Guide]::ForegroundPid() -ne $proc.Id) {
        # A real click on its own empty title-bar space brings it to the front
        $r = New-Object Guide+RECT
        [Guide]::GetWindowRect($h, [ref]$r) | Out-Null
        $tx = [int]($r.L + ($r.R - $r.L) * 0.6); $ty = $r.T + (Px 20)
        if ([Guide]::PidOf([Guide]::WindowAt($tx, $ty)) -eq $proc.Id) {
            [Guide]::SetCursorPos($tx, $ty) | Out-Null; Pump 0.1
            [Guide]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [Guide]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
            Pump 0.4
        }
    }

    if ([Guide]::ForegroundPid() -ne $proc.Id) { throw "Explorer2 is not in front; stopping so no keys reach another program" }
}
function Key([string[]]$chord, [double]$after = 0.6) {
    Front
    foreach ($k in $chord) { [Guide]::keybd_event([byte]$vk[$k], 0, 0, [IntPtr]::Zero) }
    [array]::Reverse($chord)
    foreach ($k in $chord) { [Guide]::keybd_event([byte]$vk[$k], 0, 2, [IntPtr]::Zero) }
    Pump $after
}
function TypeText([string]$text) { Front; [System.Windows.Forms.SendKeys]::SendWait($text); Pump 0.6 }

# Real mouse-move input (MOUSEEVENTF_MOVE | ABSOLUTE): hover effects need it
function MoveTo([int]$x, [int]$y, [int]$steps = 15) {
    $p = [System.Windows.Forms.Cursor]::Position
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    for ($i = 1; $i -le $steps; $i++) {
        $nx = [int](($p.X + ($x - $p.X) * $i / $steps) * 65535 / ($screen.Width - 1))
        $ny = [int](($p.Y + ($y - $p.Y) * $i / $steps) * 65535 / ($screen.Height - 1))
        [Guide]::mouse_event(0x8001, $nx, $ny, 0, [IntPtr]::Zero)
        Pump 0.02
    }
}
function OnDemo([int]$x, [int]$y) { [Guide]::PidOf([Guide]::WindowAt($x, $y)) -eq $proc.Id }
function Click([int]$x, [int]$y, [switch]$Right) {
    Front; MoveTo $x $y; Pump 0.2
    if (-not (OnDemo $x $y)) { throw "the point to click is not Explorer2; stopping" }
    if ($Right) { [Guide]::mouse_event(0x8, 0, 0, 0, [IntPtr]::Zero); [Guide]::mouse_event(0x10, 0, 0, 0, [IntPtr]::Zero) }
    else { [Guide]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [Guide]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero) }
    Pump 0.8
}

# Drags from the demo file list onto a Favorites list. Drops only when the point under the cursor
# is a Favorites list; otherwise cancels the drag with Esc and stops.
function DragToFavorites([int]$fromX, [int]$fromY, [int]$toX, [int]$toY) {
    Front
    if (-not (OnDemo $fromX $fromY)) { throw "drag source is not Explorer2; stopping" }
    MoveTo $fromX $fromY; Pump 0.3
    [Guide]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); Pump 0.2
    MoveTo ($fromX + 12) ($fromY + 4) 4; Pump 0.2          # past the drag threshold
    MoveTo $toX $toY 30; Pump 1.0
    $target = [Guide]::WindowAt($toX, $toY)
    if ([Guide]::PidOf($target) -eq $proc.Id -and [Guide]::TextOf($target) -eq "Explorer2 Favorites") {
        [Guide]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero); Pump 1.2
    }
    else {
        [Guide]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero); [Guide]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero); Pump 0.3
        [Guide]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
        throw "the drop point is not the Favorites list (it is '$([Guide]::TextOf($target))'); drag cancelled"
    }
}

# A UI Automation element of the demo Explorer2 by name (menus are top-level windows of its own)
function FindByName([string]$name, [double]$timeout = 3) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)))
    $end = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $end) {
        $e = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($e) { return $e }
        Pump 0.2
    }
    throw "'$name' not found"
}
# A row of the file list by its name: the point on its name, as screen coordinates
function FileItem([string]$name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)))
    $e = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $e) { throw "file '$name' not found in the list" }
    $b = $e.Current.BoundingRectangle
    @([int]($b.X + 70), [int]($b.Y + $b.Height / 2))
}
function Center($element) {
    $b = $element.Current.BoundingRectangle
    @([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2))
}
function PointAt($element) { $c = Center $element; MoveTo $c[0] $c[1]; Pump 0.6; $c }
function ClickElement($element) { $c = Center $element; Click $c[0] $c[1] }

# Window-relative points (pixels from the window's top-left, scaled)
$client = Px 7   # invisible resize border
function At([double]$x, [double]$y) { @(($X0 + $client + (Px $x)), ($Y0 + (Px $y))) }

# Favorites rows in the navigation pane (window-relative y, at 100% scale: six top rows, then these)
$navX = 60
$navRow = @{ Projects = 316; Media = 344 }

# ---------- record ----------
$ffmpeg = $null
if (-not $NoRecord) {
    $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
    if (-not $ff) { $ff = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\ffmpeg.exe" }
    if (-not (Test-Path $ff)) { throw "ffmpeg not found (winget install Gyan.FFmpeg)" }
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo $ff
    # Without the window's invisible 7px resize border (left, right, bottom)
    $b = Px 7
    $psi.Arguments = "-y -f gdigrab -framerate 30 -draw_mouse 1 -offset_x $($X0 + $b) -offset_y $Y0 -video_size $($WinW - 2 * $b)x$($WinH - $b) -i desktop " +
                     "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -vf scale=trunc(iw/2)*2:trunc(ih/2)*2 `"$Out`""
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.CreateNoWindow = $true
    $psi.Environment["FFREPORT"] = "file=" + ("$Out.log" -replace '\\', '/' -replace ':', '\:') + ":level=24"
    $ffmpeg = [System.Diagnostics.Process]::Start($psi)
    Pump 1.5
}

$native = $null
try {
    # ===== 1. File Explorer, with faster selection =====
    $p = At ($WinW - 300) ($WinH - 200); MoveTo $p[0] $p[1]   # keep the pointer away from the items
    Say "Explorer2 looks and works like File Explorer, with a few speed-ups" 3.5
    Say "Arrows move. Space selects the item and moves to the next one" 1
    Key Down 0.5; Key Space 0.7; Key Space 0.7; Key Down 0.5; Key Space 1.2
    Say "Space again deselects (Up, then Space)" 1
    Key Up 0.8; Key Space 1.5
    Say "Esc clears the selection" 1
    Key Esc 1.5
    Say "* (Num *) with nothing selected selects everything" 1
    Key Multiply 1.8
    Say "... and with a selection, * inverts it" 1
    Key Esc 0.4; Key Home 0.3; Key Space 0.5; Key Space 1; Key Multiply 1.8
    Key Esc 1

    # ===== 2. Environment =====
    Say "Preferences: in the ... menu (and the tray icon)" 1
    ClickElement (FindByName "See more")
    PointAt (FindByName "Preferences") | Out-Null; Pump 1.5
    Say "Make Explorer2 the default File Explorer, start in split mode, pick the theme..." 1
    PointAt (FindByName "Replace Default File Explorer") | Out-Null; Pump 1.5
    PointAt (FindByName "Theme") | Out-Null; Pump 1.2
    Say "The theme changes at once" 1
    ClickElement (FindByName "Dark")
    Pump 3
    Place
    Pump 1.5

    Say "File Explorer windows get an Explorer2 button: one click opens the folder in split mode" 1
    $native = Start-Process explorer.exe -ArgumentList "`"$($backup.FullName)`"" -PassThru
    $nw = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and $nw -eq [IntPtr]::Zero; $i++) {
        Pump 0.25
        $nw = [Guide]::MainWindowOf([uint32](Get-Process explorer | Where-Object { [Guide]::MainWindowOf([uint32]$_.Id, "Backup") -ne [IntPtr]::Zero } | Select-Object -First 1).Id, "Backup")
    }
    if ($nw -ne [IntPtr]::Zero) {
        [Guide]::MoveWindow($nw, $X0 + (Px 200), $Y0 + (Px 120), $WinW - (Px 400), $WinH - (Px 300), $true) | Out-Null
        Pump 2.5
        # Point at the Explorer2 button, left of the native caption buttons (pointing only, no click)
        $nr = New-Object Guide+RECT
        [Guide]::GetWindowRect($nw, [ref]$nr) | Out-Null
        MoveTo ($nr.R - (Px 168)) ($nr.T + (Px 16)) 25
        Pump 3
        [Guide]::PostMessage($nw, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE: closes only that window
        Pump 1
    }

    # ===== 3. Favorites =====
    Say "Favorites: a real favorites list, not just pins" 3
    Say "Drag a file or folder from the files into the list (or right-click it > Pin to Favorites)" 1
    $s = FileItem "Notes.txt"; $t = At $navX ($navRow.Media + 10)
    DragToFavorites $s[0] $s[1] $t[0] $t[1]
    Pump 1
    Say "Drop on a favorite to nest it under it" 1
    $s = FileItem "Music"; $t = At $navX $navRow.Projects
    DragToFavorites $s[0] $s[1] $t[0] $t[1]
    Pump 1
    Say "Nested favorites fly out as you point at them..." 1
    $p = At $navX $navRow.Projects; MoveTo $p[0] $p[1]; Pump 2
    $p = At 200 350; MoveTo $p[0] $p[1]; Pump 2
    Say "... and a click opens the folder in the active pane" 1
    $p = At 240 410; Click $p[0] $p[1]
    Pump 2
    Say "Rename a favorite without renaming the real folder" 1
    $p = At $navX $navRow.Projects; Click $p[0] $p[1] -Right
    ClickElement (FindByName "Rename")
    Pump 0.5; Key @("Ctrl", "A") 0.2; TypeText "Client work"; Key Enter 2
    Say "Group related folders and files" 1
    $p = At $navX $navRow.Media; Click $p[0] $p[1] -Right
    ClickElement (FindByName "New group")
    Pump 0.5; Key @("Ctrl", "A") 0.2; TypeText "Reading"; Key Enter 2

    # ===== 4. Two panes =====
    Say "Split mode (Ctrl+Shift+S): two folders side by side" 1
    $p = At $navX $navRow.Projects; Click $p[0] $p[1]
    Pump 1
    Key @("Ctrl", "Shift", "S") 2.5
    Say "Tab switches between the panes" 1
    Key Tab 1.2; Key Tab 1.2
    Say "Mark files with Space, then F5 copies them to the other pane" 1
    # Empty space below the left pane's files: puts the keyboard there without selecting anything
    $last = FileItem "Readme.txt"; Click $last[0] ($last[1] + (Px 80))
    Key Home 0.3
    for ($i = 0; $i -lt 4; $i++) { Key Down 0.15 }
    Key Space 0.5; Key Space 1
    $bar = FindByName "Function keys"
    $bb = $bar.Current.BoundingRectangle
    $bw = ($bb.Width - 10) / 6
    Click ([int]($bb.X + 2 * ($bw + 2) + $bw / 2)) ([int]($bb.Y + $bb.Height / 2))
    Pump 1.5
    # Copy only when the dialog's destination is the demo Backup folder
    # (an owned dialog is listed under its main window, so search all descendants)
    $dialog = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)),
            (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)),
            (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, "Copy")))))
    $edit = if ($dialog) {
        $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    }
    $dest = if ($edit) { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { "" }
    if ($dest.TrimEnd('\') -eq $backup.FullName) { Key Enter 3 } else { Key Esc 1; Write-Warning "copy destination was '$dest'; cancelled" }
    Say "F3 view, F4 edit, F5 copy, F6 move, F7 new folder, F8 delete: one key each" 4
    Say "Explorer2: one window, two panes" 3
}
finally {
    if ($ffmpeg) {
        if ($ffmpeg.HasExited) { Write-Warning "ffmpeg stopped early (exit $($ffmpeg.ExitCode)); see $Out.log" }
        else { $ffmpeg.StandardInput.Write("q"); $ffmpeg.WaitForExit(15000) | Out-Null }
    }
    $caption.Close()
    if (-not $proc.HasExited) { $proc.Kill() }
}
if (-not $NoRecord) { "Saved $Out" }
