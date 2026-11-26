using System.Runtime.InteropServices;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Drop target of a Favorites list. Folders and files dragged from anywhere are added as shortcuts
/// where the insertion line shows (or into the group under the mouse); a Favorites entry dragged
/// within the lists is moved. Dropped onto the middle of a folder shortcut, the items go into that
/// folder (copy / move, handled by the folder itself as in the folder tree).
/// </summary>
[ComVisible(true)]
internal sealed class FavoritesDropTarget : ShellBrowserInterop.IDropTarget
{
    private const int S_OK = 0;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;
    private static readonly Guid IidIDropTarget = new("00000122-0000-0000-C000-000000000046");

    private readonly FavoritesView _view;
    private readonly ShellBrowserInterop.IDropTargetHelper? _helper;
    private IntPtr _dataObject;
    private List<string> _paths = new();
    private bool _external;
    private bool _helperEntered;

    private ShellBrowserInterop.IDropTarget? _folderTarget;
    private string? _folderTargetPath;

    public FavoritesDropTarget(FavoritesView view)
    {
        _view = view;
        try
        {
            Type? helperType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidDragDropHelper);
            _helper = helperType is null ? null : Activator.CreateInstance(helperType) as ShellBrowserInterop.IDropTargetHelper;
        }
        catch
        {
            _helper = null;
        }
    }

    public int DragEnter(IntPtr pDataObj, uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        ReleaseDataObject();
        _dataObject = pDataObj;
        if (_dataObject != IntPtr.Zero)
        {
            Marshal.AddRef(_dataObject);
        }

        _external = FavoritesView.DraggedNode is null && FavoritesView.DraggedFixedId is null;
        _paths = _external ? GetPaths(pDataObj) : new List<string>();
        return Over(grfKeyState, pt, ref pdwEffect);
    }

    public int DragOver(uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect) => Over(grfKeyState, pt, ref pdwEffect);

    public int DragLeave()
    {
        _view.DragLeft();
        LeaveFolder();
        if (_helperEntered)
        {
            _helperEntered = false;
            _helper?.DragLeave();
        }

        ReleaseDataObject();
        return S_OK;
    }

    public int Drop(IntPtr pDataObj, uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        uint allowed = pdwEffect;
        uint effect = allowed;
        Over(grfKeyState, pt, ref effect);
        if (_helperEntered)
        {
            _helperEntered = false;
            _helper?.Drop(pDataObj, ref pt, effect);
        }

        int hr = S_OK;
        if (_folderTarget is not null)
        {
            // Into the folder: the folder copies or moves the items itself
            _view.DragLeft();
            pdwEffect = allowed;
            hr = _folderTarget.Drop(pDataObj, grfKeyState, pt, ref pdwEffect);
            ReleaseFolder();
        }
        else if (effect != DROPEFFECT_NONE)
        {
            _view.DropHere(_paths);
            pdwEffect = DROPEFFECT_NONE; // the dragged items themselves are not moved or copied
        }
        else
        {
            _view.DragLeft();
            pdwEffect = DROPEFFECT_NONE;
        }

        ReleaseDataObject();
        return hr;
    }

    private int Over(uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        uint allowed = pdwEffect;
        Point screen = new(pt.X, pt.Y);
        (FavoritesViewDropAction action, string? folder) = Plan(screen);

        uint effect;
        if (action == FavoritesViewDropAction.IntoFolder && folder is not null && _external)
        {
            if (!string.Equals(folder, _folderTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                LeaveFolder();
                _folderTarget = BindDropTarget(folder);
                _folderTargetPath = folder;
                effect = allowed;
                if (_folderTarget is null || _folderTarget.DragEnter(_dataObject, grfKeyState, pt, ref effect) != S_OK)
                {
                    ReleaseFolder();
                    effect = DROPEFFECT_NONE;
                }
            }
            else if (_folderTarget is not null)
            {
                effect = allowed;
                _folderTarget.DragOver(grfKeyState, pt, ref effect);
            }
            else
            {
                effect = DROPEFFECT_NONE;
            }
        }
        else
        {
            LeaveFolder();
            if (action is FavoritesViewDropAction.Insert or FavoritesViewDropAction.IntoGroup && (!_external || _paths.Count > 0))
            {
                effect = !_external
                    ? allowed & DROPEFFECT_MOVE
                    : (allowed & DROPEFFECT_LINK) != 0 ? DROPEFFECT_LINK : allowed & DROPEFFECT_COPY;
            }
            else
            {
                effect = DROPEFFECT_NONE;
            }
        }

        if (!_helperEntered)
        {
            _helperEntered = true;
            _helper?.DragEnter(_view.Handle, _dataObject, ref pt, effect);
        }
        else
        {
            _helper?.DragOver(ref pt, effect);
        }

        pdwEffect = effect;
        return S_OK;
    }

    private (FavoritesViewDropAction, string?) Plan(Point screen) => _view.DragOverAt(screen, _external);

    private void LeaveFolder()
    {
        if (_folderTarget is not null)
        {
            try
            {
                _folderTarget.DragLeave();
            }
            catch
            {
                // Folder target gone
            }

            ReleaseFolder();
        }
        else
        {
            _folderTargetPath = null;
        }
    }

    private void ReleaseFolder()
    {
        if (_folderTarget is not null)
        {
            Marshal.ReleaseComObject(_folderTarget);
            _folderTarget = null;
        }

        _folderTargetPath = null;
    }

    private static ShellBrowserInterop.IDropTarget? BindDropTarget(string path)
    {
        Guid iidItem = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out ShellBrowserInterop.IShellItem? item) != 0 ||
            item is null)
        {
            return null;
        }

        try
        {
            Guid bhid = ShellBrowserInterop.BhidSFUIObject;
            Guid iid = IidIDropTarget;
            if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out IntPtr ppv) != 0 || ppv == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(ppv) as ShellBrowserInterop.IDropTarget;
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Paths of the dragged items: file-system paths, or shell parsing names for other places.</summary>
    internal static List<string> GetPaths(IntPtr dataObject)
    {
        List<string> paths = new();
        if (dataObject == IntPtr.Zero)
        {
            return paths;
        }

        Guid iid = ShellBrowserInterop.IidIShellItemArray;
        if (ShellBrowserInterop.SHCreateShellItemArrayFromDataObject(dataObject, ref iid, out ShellBrowserInterop.IShellItemArray? array) != 0 ||
            array is null)
        {
            return paths;
        }

        try
        {
            if (array.GetCount(out uint count) != 0)
            {
                return paths;
            }

            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out ShellBrowserInterop.IShellItem? item) != 0 || item is null)
                {
                    continue;
                }

                try
                {
                    if (PathOf(item) is { } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        catch
        {
            // Unusual data: whatever was read so far
        }
        finally
        {
            Marshal.ReleaseComObject(array);
        }

        return paths;
    }

    internal static string? PathOf(ShellBrowserInterop.IShellItem item)
    {
        try
        {
            if (item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? fsPath) == 0 && !string.IsNullOrWhiteSpace(fsPath))
            {
                return fsPath;
            }
        }
        catch
        {
            // Not a file-system item
        }

        try
        {
            return item.GetDisplayName(ShellBrowserInterop.SIGDN_DESKTOPABSOLUTEPARSING, out string? parsing) == 0 &&
                   !string.IsNullOrWhiteSpace(parsing)
                ? parsing
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void ReleaseDataObject()
    {
        if (_dataObject != IntPtr.Zero)
        {
            Marshal.Release(_dataObject);
            _dataObject = IntPtr.Zero;
        }
    }
}

internal enum FavoritesViewDropAction { None, Insert, IntoGroup, IntoFolder }
