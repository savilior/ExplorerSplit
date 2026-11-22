using System.Runtime.InteropServices;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>The navigation tree's pinned Quick access roots, as seen by the drop target.</summary>
internal interface IPinnedRoots
{
    int PinnedCount { get; }

    /// <summary>Tree item of the pinned folder at <paramref name="index"/>.</summary>
    IntPtr PinnedItemAt(int index);

    /// <summary>Index of the pinned root <paramref name="hItem"/>, or -1.</summary>
    int PinnedIndexOfItem(IntPtr hItem);

    /// <summary>
    /// Puts the folder <paramref name="path"/> at the insertion point before the pinned root
    /// <paramref name="insertIndex"/> (or after the last one when it equals the count).
    /// </summary>
    void DropAt(string path, int insertIndex);
}

/// <summary>
/// Wraps the navigation tree's own drop target. A single folder dragged near the top or bottom edge
/// of a pinned Quick access folder shows an insertion line there, as in File Explorer, and dropping
/// puts it at that place in Quick access. Everything else (dropping onto a folder to copy or move)
/// goes to the tree's own drop target unchanged.
/// </summary>
[ComVisible(true)]
internal sealed class QuickAccessReorderDropTarget : ShellBrowserInterop.IDropTarget
{
    private const int S_OK = 0;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;
    private const uint SFGAO_STREAM = 0x00400000;
    private const uint SFGAO_FOLDER = 0x20000000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint TVM_GETITEMRECT = 0x1104;
    private const uint TVM_HITTEST = 0x1111;
    private const uint TVM_SETINSERTMARK = 0x111A;
    private const uint TVM_SETINSERTMARKCOLOR = 0x1125;

    [StructLayout(LayoutKind.Sequential)]
    private struct TVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public IntPtr hItem;
    }

    private readonly IntPtr _treeHwnd;
    private readonly ShellBrowserInterop.IDropTarget _inner;
    private readonly IPinnedRoots _roots;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private ShellBrowserInterop.IDropTargetHelper? _helper;

    private IntPtr _dataObject;
    private string? _dragFolder;
    private bool _innerEntered;
    private bool _helperEntered;
    private int _insertIndex = -1;

    public QuickAccessReorderDropTarget(IntPtr treeHwnd, ShellBrowserInterop.IDropTarget inner, IPinnedRoots roots, Color markColor)
    {
        _treeHwnd = treeHwnd;
        _inner = inner;
        _roots = roots;
        NativeMethods.SendMessage(_treeHwnd, TVM_SETINSERTMARKCOLOR, IntPtr.Zero, new IntPtr(ColorTranslator.ToWin32(markColor)));

        try
        {
            Type? helperType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidDragDropHelper);
            _helper = helperType is null ? null : Activator.CreateInstance(helperType) as ShellBrowserInterop.IDropTargetHelper;
        }
        catch
        {
            _helper = null; // no drag image while reordering
        }
    }

    public ShellBrowserInterop.IDropTarget Inner => _inner;

    public int DragEnter(IntPtr pDataObj, uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        ReleaseDataObject();
        _dataObject = pDataObj;
        if (_dataObject != IntPtr.Zero)
        {
            Marshal.AddRef(_dataObject);
        }

        _dragFolder = GetSingleFolder(pDataObj);
        _insertIndex = -1;
        return Over(grfKeyState, pt, ref pdwEffect);
    }

    public int DragOver(uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect) => Over(grfKeyState, pt, ref pdwEffect);

    public int DragLeave()
    {
        HideMark();
        LeaveHelper();
        int hr = S_OK;
        if (_innerEntered)
        {
            _innerEntered = false;
            hr = _inner.DragLeave();
        }

        ReleaseDataObject();
        return hr;
    }

    public int Drop(IntPtr pDataObj, uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        int insertIndex = _insertIndex;
        string? folder = _dragFolder;
        HideMark();

        if (insertIndex >= 0 && folder is not null)
        {
            if (_helperEntered)
            {
                _helper?.Drop(pDataObj, ref pt, DROPEFFECT_NONE);
                _helperEntered = false;
            }

            // Report "nothing done" to the drag source: the folder itself is not moved
            pdwEffect = DROPEFFECT_NONE;
            ReleaseDataObject();

            // After DoDragDrop has returned
            if (_uiContext is not null)
            {
                _uiContext.Post(_ => _roots.DropAt(folder, insertIndex), null);
            }
            else
            {
                _roots.DropAt(folder, insertIndex);
            }

            return S_OK;
        }

        LeaveHelper();
        int hr;
        if (!_innerEntered)
        {
            uint effect = pdwEffect;
            _inner.DragEnter(pDataObj, grfKeyState, pt, ref effect);
        }

        _innerEntered = false;
        hr = _inner.Drop(pDataObj, grfKeyState, pt, ref pdwEffect);
        ReleaseDataObject();
        return hr;
    }

    private int Over(uint grfKeyState, NativeMethods.POINT pt, ref uint pdwEffect)
    {
        int insertIndex = _dragFolder is null ? -1 : HitInsertIndex(pt);
        if (insertIndex >= 0)
        {
            // pdwEffect comes in as the effects the drag source allows; show one of them
            uint allowed = pdwEffect;
            uint effect = (allowed & DROPEFFECT_MOVE) != 0 ? DROPEFFECT_MOVE
                : (allowed & DROPEFFECT_LINK) != 0 ? DROPEFFECT_LINK
                : allowed & DROPEFFECT_COPY;

            if (_innerEntered)
            {
                _innerEntered = false;
                _inner.DragLeave();
            }

            if (!_helperEntered)
            {
                _helper?.DragEnter(_treeHwnd, _dataObject, ref pt, effect);
                _helperEntered = true;
            }
            else
            {
                _helper?.DragOver(ref pt, effect);
            }

            ShowMark(insertIndex);
            pdwEffect = effect;
            return S_OK;
        }

        HideMark();
        LeaveHelper();
        if (!_innerEntered)
        {
            _innerEntered = true;
            return _inner.DragEnter(_dataObject, grfKeyState, pt, ref pdwEffect);
        }

        return _inner.DragOver(grfKeyState, pt, ref pdwEffect);
    }

    /// <summary>
    /// Position in the pinned list (0..count) for a point in the top or bottom quarter of a pinned
    /// root, or -1 elsewhere (the middle of an item means "drop into this folder").
    /// </summary>
    private int HitInsertIndex(NativeMethods.POINT screenPt)
    {
        NativeMethods.POINT client = screenPt;
        NativeMethods.ScreenToClient(_treeHwnd, ref client);

        TVHITTESTINFO ht = new() { pt = client };
        IntPtr htPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TVHITTESTINFO>());
        try
        {
            Marshal.StructureToPtr(ht, htPtr, false);
            NativeMethods.SendMessage(_treeHwnd, TVM_HITTEST, IntPtr.Zero, htPtr);
            ht = Marshal.PtrToStructure<TVHITTESTINFO>(htPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(htPtr);
        }

        int index = ht.hItem == IntPtr.Zero ? -1 : _roots.PinnedIndexOfItem(ht.hItem);
        if (index < 0 || !TryGetItemRect(ht.hItem, out Rectangle rect))
        {
            return -1;
        }

        int zone = Math.Max(4, rect.Height / 4);
        if (client.Y < rect.Top + zone)
        {
            return index;
        }

        if (client.Y >= rect.Bottom - zone)
        {
            return index + 1;
        }

        return -1;
    }

    private bool TryGetItemRect(IntPtr hItem, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        // TVM_GETITEMRECT takes the item handle in the first bytes of the RECT buffer
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteIntPtr(buffer, hItem);
            if (NativeMethods.SendMessage(_treeHwnd, TVM_GETITEMRECT, IntPtr.Zero, buffer) == IntPtr.Zero)
            {
                return false;
            }

            int left = Marshal.ReadInt32(buffer, 0);
            int top = Marshal.ReadInt32(buffer, 4);
            int right = Marshal.ReadInt32(buffer, 8);
            int bottom = Marshal.ReadInt32(buffer, 12);
            rect = Rectangle.FromLTRB(left, top, right, bottom);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ShowMark(int insertIndex)
    {
        if (insertIndex == _insertIndex)
        {
            return;
        }

        _insertIndex = insertIndex;
        int count = _roots.PinnedCount;
        if (insertIndex < count)
        {
            NativeMethods.SendMessage(_treeHwnd, TVM_SETINSERTMARK, IntPtr.Zero, _roots.PinnedItemAt(insertIndex));
        }
        else if (count > 0)
        {
            NativeMethods.SendMessage(_treeHwnd, TVM_SETINSERTMARK, new IntPtr(1), _roots.PinnedItemAt(count - 1));
        }
    }

    private void HideMark()
    {
        if (_insertIndex >= 0)
        {
            _insertIndex = -1;
            NativeMethods.SendMessage(_treeHwnd, TVM_SETINSERTMARK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void LeaveHelper()
    {
        if (_helperEntered)
        {
            _helperEntered = false;
            _helper?.DragLeave();
        }
    }

    private void ReleaseDataObject()
    {
        if (_dataObject != IntPtr.Zero)
        {
            Marshal.Release(_dataObject);
            _dataObject = IntPtr.Zero;
        }

        _dragFolder = null;
    }

    /// <summary>Parsing path of the dragged item when it is exactly one folder (not a zip), else null.</summary>
    private static string? GetSingleFolder(IntPtr dataObject)
    {
        if (dataObject == IntPtr.Zero)
        {
            return null;
        }

        Guid iid = ShellBrowserInterop.IidIShellItemArray;
        if (ShellBrowserInterop.SHCreateShellItemArrayFromDataObject(dataObject, ref iid, out ShellBrowserInterop.IShellItemArray? array) != 0 ||
            array is null)
        {
            return null;
        }

        ShellBrowserInterop.IShellItem? item = null;
        try
        {
            if (array.GetCount(out uint count) != 0 || count != 1 || array.GetItemAt(0, out item) != 0 || item is null)
            {
                return null;
            }

            if (item.GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, out uint attributes) < 0 ||
                (attributes & SFGAO_FOLDER) == 0 || (attributes & SFGAO_STREAM) != 0)
            {
                return null;
            }

            return item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out string? path) == 0 ? path : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            Marshal.ReleaseComObject(array);
        }
    }
}
