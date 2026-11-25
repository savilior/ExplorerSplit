# 1. Favorites are drawn by Explorer2, not by the shell folder tree

Date: 2026-11-25. Status: accepted.

## Context

The navigation pane's Quick access pins were shell roots in the native folder tree
(`INameSpaceTreeControl`). The user wanted a Favorites section with groups (nested), hover
flyouts, a per-row "open in new tab" icon, and fast reordering. The shell tree can only show real
shell items: it has no place for groups, flyouts or extra icons, and reordering pins goes through
Windows' slow, undocumented pin list (`Core/QuickAccessPins.cs`).

## Decision

- Favorites are Explorer2's own list (`Core/Favorites.cs`, saved in `%APPDATA%\Explorer2\favorites.json`),
  imported once from the Quick access pins. Quick access is never written.
- The list is a control Explorer2 draws (`UI/FavoritesView.cs`) above the shell tree, using the
  tree's own theme parts (`Explorer::TreeView`) and the system image list, so it looks like the tree.
  Home and Gallery move into it (they sat above the pins); the shell tree now starts at OneDrive.
- "Pin to Favorites" in the file view's right-click menu is added inside Explorer2 only (no shell
  extension, no registry): the hosted view's menu is caught on `WM_INITMENUPOPUP`, the line is
  owner-drawn like the view's own lines, and has command id 0 so the view ignores it; a menu message
  filter notices the pick (`UI/FavoritesShellMenus.cs`). The folder tree's menu gets it through
  `INameSpaceTreeControlEvents.OnBeforeContextMenu`.
- The setting `FavoritesSection` (tray: Favorites in Navigation Pane) turns it off and brings back
  the native pins.

## Consequences

- Two scroll areas: a long Favorites list scrolls on its own (it takes at most 60% of the pane).
- OneDrive is now below the Favorites instead of above them.
- The file-view menu hook depends on how the hosted view shows its menu (owner window, owner-draw
  layout measured at 24 px rows / 31 px text inset). If Windows changes that, the line may look off
  or be missing; the tree menu and drag and drop still work.
