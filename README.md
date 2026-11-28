# Explorer2

A Windows 11 File Explorer replacement with **tabs, a side-by-side split view and two-pane file manager keyboard control** — built on the native Windows shell, so file lists, context menus, thumbnails, drag & drop, previews and sharing all behave exactly like File Explorer.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![License](https://img.shields.io/badge/license-MIT-green)

## Why Explorer2

Explorer2 looks and works like the Windows File Explorer you already know: the same file views, menus, search and shortcuts, plus a few enhancements.

The main difference is **Split mode**: two file panes side by side in one window. Copying or moving files between two folders no longer means opening a second File Explorer window and arranging the two windows next to each other. Open both folders in one window and drag files across, or press **F5** to copy and **F6** to move to the other pane.

Split mode and its keyboard control are inspired by the dual-pane file managers of the 1980s.

## Features

- **Native file views** — each tab hosts the real Windows shell view, not a re-implementation.
- **Tabs** in the title bar, Windows 11 style; drag tabs between panes.
- **Split mode** — two panes side by side, each with its own tabs (toggle with **Ctrl+Shift+S** or the **Split** button).
- **Two-pane file manager marking in both modes**: Space/Insert to mark, Num * to invert, and the arrow keys move a visible cursor without dropping the marks.
- **Two-pane file manager function keys in split mode**: F3 viewer, F4 edit, F5 copy, F6 move, F7 new folder, F8 delete, with a function-key bar in the status bar.
- **Favorites in the navigation pane** (instead of the Quick access pins): folders and files you pin, in groups (also groups inside groups). Rest the mouse on a group and its contents open to the side; click to open in the active pane, or click the small icon at the right of a folder to open it in a new tab. Drag to add and reorder. See [Favorites](#favorites).
- **Recent folders**: the arrow at the right end of the address bar lists the folders you opened lately (newest first, kept between sessions, with **Clear history**).
- **Taskbar right-click**: like File Explorer's pinned folders, right-clicking Explorer2 on the taskbar lists your Favorites (each top-level group under its own heading) and recent folders; each opens in a new window.
- **A clear cursor in the file list**: after keyboard use, the current item gets a bold outline (black, or white in dark mode), easy to see even on a multiple selection.
- **Reorder Quick access in the navigation pane** (with Favorites turned off): drag a pinned folder to the top or bottom edge of another pinned folder; a line shows where it will go. The pane shows the new order at once and Windows' pin list is updated in the background (a few seconds for a long move). Changes made in File Explorer or other apps appear in open Explorer2 windows right away.
- **F3 quick viewer** — native previews for PDF and Office files, images, text (UTF-8 / UTF-16 / ANSI) and hex.
- **Preview pane** using the registered Windows preview handlers (PDF, Word, Excel, PowerPoint, mail, …).
- **Native Share sheet**, Sort / Group by / View menus, breadcrumb address bar.
- **See more (…) menu like File Explorer's** — Undo / Redo, Pin to Quick access, Compress to ZIP file, Add to favorites, Copy path, selection commands, Properties and Options; on **This PC** also Map / Disconnect network drive, Add a network location and Connect to a media server. Explorer2 adds Open in Terminal and, in split mode, Sync other pane to this folder.
- **Native search in the active pane** — the same query engine as File Explorer (names, properties, `kind:`, `date:`, `size:` ...), updated as you type, with the **Search options** menu (Current folder / All subfolders, Date modified, Kind, Size, Other properties, Advanced options) and **Close search**. Advanced options are File Explorer's own settings; File contents applies at once, System files and Zipped folders after Explorer2 restarts.
- **Optional default-Explorer replacement** — open folders and **Win+E** in Explorer2.
- **Follows File Explorer's Folder Options** — new windows and tabs open to the *Open File Explorer to* location (Home, This PC or Downloads); hidden files, extensions, check boxes and the other view options come from the native views.
- **Explorer2 button on File Explorer's title bar** — click the Explorer2 icon on a native File Explorer window to open that folder in Explorer2's split view.
- **Tray app** — runs in the background, opens new windows instantly, optional start with Windows.
- Light and dark mode follow the Windows theme, or pick one in the tray menu or **...** → **Preferences** (**Theme**); it applies at once.
- **Guided tour** — **...** → **Take the tour** shows the main features in a demo window, with a voice and captions. See [Guided tour](#guided-tour).

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

### Guided tour

The tour starts by itself the first time Explorer2 runs for a new user (only once). Later, **...** → **Take the tour** (or the tray menu → **Take the Tour**) opens it again. It opens a demo window that shows the main features in four chapters: faster selection, your work environment, Favorites and the two panes. The tour works the app by itself, points at each thing with a drawn cursor and a highlight, and reads each step aloud with captions.

It runs on sample folders in `%TEMP%\Explorer2Tour`, as a separate demo Explorer2 with its own settings and Favorites, so your files, settings and Favorites are never touched. It sends no real keys or mouse clicks, so your mouse stays yours while it plays.

| Control | What it does |
|---|---|
| Bar at the bottom right | **Back** (restart the chapter, or the one before), **Play / Pause**, **Next chapter**, **Captions** on/off, **Voice** on/off, **Close** |
| **Esc** | Ends the tour |
| Another program in front | The tour pauses and hides its drawings; it goes on when you come back |

The English voice is recorded with [Kokoro](https://github.com/hexgrad/kokoro) (Apache 2.0), an offline neural voice, and ships as small MP3 files in `Tour\voice\en-US`. A line with no recording of its current text, such as a translated or edited one, is read by a Windows voice (installed under **Settings → Time & language → Speech**). After editing lines, run `tools\render-tour-voice.py` to record them again (setup steps are at the top of the script). The tour's texts are in `Tour\tour.en.json` next to `Explorer2.exe`. To translate them, copy it to `tour.<language>.json` (for example `tour.de.json`) and translate the texts. The tour picks the file for the Windows display language and the voice for that language. Running `Explorer2.exe --take-tour` starts the tour from a shortcut or the command line.

### Windows and tabs

| Action | How |
|---|---|
| New window | Tray icon → **New Explorer2 Window**, or double-click the tray icon |
| New tab | **Ctrl+T**, or the **+** after the tabs |
| Close tab | **Ctrl+W**, the tab's **×**, or middle-click the tab |
| Next / previous tab | **Ctrl+Tab** / **Ctrl+Shift+Tab** |
| Move a tab to the other pane | Drag it across in the title bar, or right-click it → **Move tab to right/left pane** |
| Tab menu | Right-click a tab: **New tab**, **Duplicate tab**, **Close tab**, **Close other tabs**, **Close tabs to the right** |
| Window menu / About | Click the Explorer2 icon at the top-left, right-click empty title-bar space, or **Alt+Space** |
| Snap layouts | Rest the mouse on the Maximize button (or **Win+Z**) |

Closing the last tab of one pane in split mode closes that pane (the other pane's tabs stay); closing the last tab in normal mode closes the window.

### Navigation

| Action | Keys |
|---|---|
| Back / Forward | **Alt+Left** / **Alt+Right** |
| Up one folder | **Backspace** or **Alt+Up** |
| Edit the address | **Ctrl+L**, **Alt+D** (and **F4** in normal mode) |
| Address bar menu | Right-click the address bar: **Copy address as text**, **Edit address**, **Delete history** (on a folder name also **Open in new tab**) |
| Clear the selection | **Esc** in the file list |
| Search the current folder (Windows search, results in the same tab, updates as you type) | **Ctrl+F** (and **F3** in normal mode); **Esc**, the **X** or **Close search** ends it |
| Refresh | **F5** in normal mode |
| Rename | **F2** |
| New folder | **Ctrl+Shift+N** |
| Properties | **Alt+Enter** |
| Move between the navigation pane and the files (normal mode) | **Tab** / **Shift+Tab** |
| Go to the navigation pane | **Ctrl+Shift+E**; **Tab** returns to the files |

A new window starts with the keyboard in the file list.

### Favorites

The top of the navigation pane shows fixed rows: **Home**, **Gallery**, the **Recycle Bin** (drop files on it to delete them, right-click → **Empty Recycle Bin**), **Desktop**, **Downloads** and **Documents**. Drag them to change their order; turn each on or off with **Navigation Pane Top Section...** in the tray menu. Under a divider come your **Favorites**, in place of File Explorer's Quick access pins. The first time it starts with a copy of your Quick access pins; after that it is Explorer2's own list (Quick access itself is never changed). It is saved in `%APPDATA%\Explorer2\favorites.json` and shared by all Explorer2 windows.

Favorites are links: adding, moving or removing them never touches the folders and files themselves. A link to a folder can hold nested links. Below the Favorites, the folder tree lists the sources (OneDrive, This PC, Network), folded.

| To | Do |
|---|---|
| Open a favorite in the active pane | Click it (a file opens with its app) |
| Open a folder in a new tab | Click the icon at the right of the row (it shows when the mouse is on the row), middle-click, or **Ctrl+Enter** |
| Open a folder in the other pane (split mode) | Right-click → **Open in other pane** |
| See a group's contents, or a link's nested links | Rest the mouse on it: they open to the side and close when the mouse leaves (each level opens right below its row, indented, like a tree). Click its arrow to open it in place instead (a group also opens on a click) |
| Add folders or files | Drag them from anywhere onto the list (a line shows where they go), or right-click them in the files or the folder tree → **Pin to Favorites** |
| Nest links in a group or a folder link | Drop onto the middle of it. Hold the drag over one that already has entries and its flyout opens, to drop at an exact place there (or in a deeper flyout) |
| Reorder, or move into / out of a group or link | Drag the favorite (also into and out of the side flyouts) |
| Make a group | Right-click → **New group** (or **New group inside** on a group) |
| Rename / remove | Right-click → **Rename** (**F2**) / **Unpin from Favorites** (**Del**). Removing a favorite never touches the folder or file itself |
| Use the keyboard | **Ctrl+Shift+E**, then the arrow keys (a folder opens as you move onto it), **Right** / **Left** open and close a group or a link's nested links, **Enter** opens. **Down** at the end continues into the folder tree |

A favorite whose folder or file can't be found (moved, deleted, drive not connected) is shown dimmed; clicking it offers to remove it. To go back to the native Quick access pins, turn off **Favorites in Navigation Pane** in the tray menu.

### Split mode

Toggle with **Ctrl+Shift+S** (changeable from the tray menu) or the **Split** button. **Tab** switches between the left and right pane; **Ctrl+Shift+E** goes to the navigation pane and **Tab** comes back to the active pane.

| Key | Action |
|---|---|
| **Space** / **Insert** | Mark / unmark the item under the cursor and move down |
| **Up** / **Down** / **PgUp** / **PgDn** / **Home** / **End** | While items are marked, move the cursor and keep the marks |
| **Num \*** | Invert the selection |
| **F3** | View the file in the quick viewer (a folder is entered) |
| **Shift+F3** | Open with the default app |
| **F4** | Edit (the file's *Edit* command, or Notepad) |
| **F5** | Copy the selection to the other pane's folder (asks for the destination first; tick **Don't ask again** to skip that) |
| **F6** | Move the selection to the other pane's folder (asks for the destination first; tick **Don't ask again** to skip that) |
| **Shift+F6** | Rename |
| **F7** | New folder (asks for the name) |
| **F8** | Delete to the Recycle Bin |

The function keys are also buttons in the status bar. Copy and move use Windows' own progress, conflict and undo (**Ctrl+Z**) handling. When you enter a folder, the cursor starts on its first item.

The marking keys (**Space**, **Insert**, **Num \*** and the cursor keys while items are marked) also work in normal mode. A plain click starts a new selection, so the next **Space** marks the clicked item.

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

The same settings are also in every window under **...** → **Preferences**.

| Item | What it does |
|---|---|
| **New Explorer2 Window** | Opens a new window |
| **Take the Tour** | Plays the [guided tour](#guided-tour) in a demo window |
| **Replace Default File Explorer** | Opens folders and **Win+E** in Explorer2 |
| **Start in Split Mode by Default** | New windows open with two panes |
| **Always Use Details View & Group: None** | Forces Details view without grouping in every folder |
| **Enable Title Bar Toggle Button** | Shows a split button on native File Explorer windows |
| **Favorites in Navigation Pane** | On (default): the navigation pane shows Explorer2's [Favorites](#favorites). Off: File Explorer's Quick access pins. Open windows reopen in place to show it |
| **Navigation Pane Top Section...** | Turns each top row (Home, Gallery, Recycle Bin, Desktop, Downloads, Documents) on or off. Open windows update at once; drag the rows in the pane to reorder them |
| **Ask Before F5 Copy / F6 Move** | On (default): F5/F6 ask for the destination. Off (also set by **Don't ask again** in that dialog): they copy/move straight to the other pane. Windows still asks before overwriting, and Ctrl+Z undoes |
| **Hotkey** | Changes the split-mode hotkey (default **Ctrl+Shift+S**) |
| **Default Right-Pane Path** | What the right pane opens: same folder as the left, Home, or a custom folder |
| **Theme** | **Use Windows Setting** (default), **Light** or **Dark**. Applies at once: open windows reopen in place with the same tabs and folders |
| **Run at Windows Startup** | Starts Explorer2 in the background when you sign in |
| **Close All Windows** / **Exit** | Close every window / quit Explorer2 |

Settings are stored in `%APPDATA%\Explorer2\settings.json`.

## Building for development

```powershell
dotnet build Explorer2.sln -c Release
```

The executable is written to `bin\Release\net8.0-windows\Explorer2.exe`.

To try a build while your installed Explorer2 keeps running, start it with `--standalone` (optionally followed by a folder). It opens one window with no tray icon and does not hand the folder to the running Explorer2.

## License

Explorer2 is released under the [MIT License](LICENSE).

## About

Made by Savvy — <https://itailiors.github.io/portfolio-hub/>
