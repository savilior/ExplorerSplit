# Explorer2

A Windows 11 File Explorer replacement with **tabs, a side-by-side split view and Total Commander style keyboard control** — built on the native Windows shell, so file lists, context menus, thumbnails, drag & drop, previews and sharing all behave exactly like File Explorer.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Native file views** — each tab hosts the real Windows shell view, not a re-implementation.
- **Tabs** in the title bar, Windows 11 style; drag tabs between panes.
- **Split mode** — two panes side by side, each with its own tabs (toggle with **Ctrl+Shift+S** or the **Split** button).
- **Total Commander style keys in split mode** — Space/Insert to mark, Num * to invert, F3 viewer, F4 edit, F5 copy, F6 move, F7 new folder, F8 delete, with a function-key bar in the status bar.
- **F3 quick viewer** (like Total Commander's Lister) — native previews for PDF and Office files, images, text (UTF-8 / UTF-16 / ANSI) and hex.
- **Preview pane** using the registered Windows preview handlers (PDF, Word, Excel, PowerPoint, mail, …).
- **Native Share sheet**, Sort / Group by / View menus, breadcrumb address bar.
- **Native search in the active pane** — the same query engine as File Explorer (names, properties, `kind:`, `date:`, `size:` ...), updated as you type, with the **Search options** menu (Current folder / All subfolders, Date modified, Kind, Size, Other properties, Advanced options) and **Close search**. Advanced options are File Explorer's own settings; File contents applies at once, System files and Zipped folders after Explorer2 restarts.
- **Optional default-Explorer replacement** — open folders and **Win+E** in Explorer2.
- **Tray app** — runs in the background, opens new windows instantly, optional start with Windows.
- Light and dark mode follow the Windows theme.

## Requirements

- Windows 10 (version 2004 / build 19041) or later, x64 — Windows 11 recommended.
- The **standalone** download needs nothing else. The smaller download and building from source need .NET 8 (see below).

## Installation

### Download (recommended)

1. Go to the [latest release](https://github.com/savilior/ExplorerSplit/releases/latest) and download one of:
   - **`Explorer2-vX.Y.Z-win-x64-standalone.zip`** — a single `Explorer2.exe` with .NET included. Nothing else to install.
   - **`Explorer2-vX.Y.Z-win-x64-requires-dotnet8.zip`** — much smaller, but needs the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
2. Extract it to a **permanent** folder, for example `%LOCALAPPDATA%\Programs\Explorer2`. Windows remembers this location for *Run at Windows Startup* and *Replace Default File Explorer*, so don't run it from the Downloads folder or a temporary location.
3. Run `Explorer2.exe`. An Explorer2 window opens and an icon appears in the notification area (system tray).

   The app isn't code-signed yet, so Windows SmartScreen may show *"Windows protected your PC"* the first time. Click **More info → Run anyway**.
4. Optional — right-click the tray icon and turn on **Run at Windows Startup** and/or **Replace Default File Explorer** (opens folders and **Win+E** in Explorer2).

### Build from source

1. **Install the .NET 8 SDK** from <https://dotnet.microsoft.com/download/dotnet/8.0> (choose *SDK x64*), or with winget:

   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```

2. **Get the source:**

   ```powershell
   git clone https://github.com/savilior/ExplorerSplit.git
   cd ExplorerSplit
   ```

   (or use **Code → Download ZIP** on GitHub and extract it).

3. **Build** into a permanent folder — Windows remembers this path for *Run at Windows Startup* and *Replace Default File Explorer*, so don't use a temporary location:

   ```powershell
   dotnet publish Explorer2.csproj -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\Explorer2"
   ```

4. **Run it:**

   ```powershell
   & "$env:LOCALAPPDATA\Programs\Explorer2\Explorer2.exe"
   ```

   An Explorer2 window opens and an icon appears in the notification area (system tray). Pin `Explorer2.exe` to Start or the taskbar if you like.

5. **Optional** — right-click the tray icon and turn on:
   - **Run at Windows Startup** to keep Explorer2 ready in the background.
   - **Replace Default File Explorer** to open folders and **Win+E** in Explorer2.

### Updating

Exit Explorer2 (tray icon → **Exit**). If you use the download, replace the files in your install folder with the new release. If you build from source, run in the source folder:

```powershell
git pull
dotnet publish Explorer2.csproj -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\Explorer2"
```

### Uninstalling

1. Right-click the tray icon and turn **off** *Replace Default File Explorer* and *Run at Windows Startup* (this restores Windows' own settings).
2. Click **Exit**.
3. Delete the install folder (`%LOCALAPPDATA%\Programs\Explorer2`) and the settings folder (`%APPDATA%\Explorer2`).

## Help

### Windows and tabs

| Action | How |
|---|---|
| New window | Tray icon → **New Explorer2 Window**, or double-click the tray icon |
| New tab | **Ctrl+T**, or the **+** after the tabs |
| Close tab | **Ctrl+W**, the tab's **×**, or middle-click the tab |
| Next / previous tab | **Ctrl+Tab** / **Ctrl+Shift+Tab** |
| Move a tab to the other pane | Drag it across in the title bar |
| Window menu / About | Click the Explorer2 icon at the top-left, or **Alt+Space** |

Closing the last tab of one pane in split mode closes that pane (the other pane's tabs stay); closing the last tab in normal mode closes the window.

### Navigation

| Action | Keys |
|---|---|
| Back / Forward | **Alt+Left** / **Alt+Right** |
| Up one folder | **Backspace** or **Alt+Up** |
| Edit the address | **Ctrl+L**, **Alt+D** (and **F4** in normal mode) |
| Search the current folder (Windows search, results in the same tab, updates as you type) | **Ctrl+F** (and **F3** in normal mode); **Esc**, the **X** or **Close search** ends it |
| Refresh | **F5** in normal mode |
| Rename | **F2** |
| New folder | **Ctrl+Shift+N** |
| Properties | **Alt+Enter** |

### Split mode (Total Commander style)

Toggle with **Ctrl+Shift+S** (changeable from the tray menu) or the **Split** button. **Tab** switches between the left and right pane.

| Key | Action |
|---|---|
| **Space** / **Insert** | Mark / unmark the item under the cursor and move down |
| **Up** / **Down** / **PgUp** / **PgDn** / **Home** / **End** | While items are marked, move the cursor and keep the marks |
| **Num \*** | Invert the selection |
| **F3** | View the file in the quick viewer (a folder is entered) |
| **Shift+F3** | Open with the default app |
| **F4** | Edit (the file's *Edit* command, or Notepad) |
| **F5** | Copy the selection to the other pane's folder (asks for the destination first) |
| **F6** | Move the selection to the other pane's folder (asks for the destination first) |
| **Shift+F6** | Rename |
| **F7** | New folder (asks for the name) |
| **F8** | Delete to the Recycle Bin |

The function keys are also buttons in the status bar. Copy and move use Windows' own progress, conflict and undo (**Ctrl+Z**) handling. When you enter a folder, the cursor starts on its first item.

> **Note:** some tools (for example keyboard-layout switchers such as LangOver) register **F6** system-wide. While an Explorer2 window in split mode is active, Explorer2 takes F6 first so *Move* still works; everywhere else F6 keeps its usual behaviour.

### Quick viewer (F3)

| Key | Action |
|---|---|
| **N** / **P** (or **Ctrl+PgDn** / **Ctrl+PgUp**) | Next / previous file in the folder |
| **1** / **3** / **4** | Text / Hex / Automatic view |
| **W** | Word wrap |
| **Enter** or **Shift+F3** | Open with the default app |
| **Esc** or **F3** | Close |

The automatic view uses the Windows previewer for PDF and Office files, shows images and text directly, and switches to hex for binary files. If you click inside a PDF/Office preview, the previewer takes the keyboard; click the viewer's status line to use the viewer keys again.

### Preview pane

Click **Preview** in the command bar. It uses the same preview handlers as File Explorer. Office does not preview files downloaded from the internet until they are unblocked (file **Properties → Unblock**).

### Tray menu

| Item | What it does |
|---|---|
| **New Explorer2 Window** | Opens a new window |
| **Replace Default File Explorer** | Opens folders and **Win+E** in Explorer2 |
| **Start in Split Mode by Default** | New windows open with two panes |
| **Always Use Details View & Group: None** | Forces Details view without grouping in every folder |
| **Enable Title Bar Toggle Button** | Shows a split button on native File Explorer windows |
| **Hotkey** | Changes the split-mode hotkey (default **Ctrl+Shift+S**) |
| **Default Right-Pane Path** | What the right pane opens: same folder as the left, Home, or a custom folder |
| **Run at Windows Startup** | Starts Explorer2 in the background when you sign in |
| **Close All Windows** / **Exit** | Close every window / quit Explorer2 |

Settings are stored in `%APPDATA%\Explorer2\settings.json`.

## Building for development

```powershell
dotnet build Explorer2.sln -c Release
```

The executable is written to `bin\Release\net8.0-windows\Explorer2.exe`.

## License

Explorer2 is released under the [MIT License](LICENSE).

## About

Made by Savvy — <https://itailiors.github.io/portfolio-hub/>
