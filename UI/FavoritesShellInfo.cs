using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Explorer2.UI;

/// <summary>Name, icon and kind of a favorite's target, as the shell shows them.</summary>
internal sealed record FavoriteTargetInfo(string DisplayName, int IconIndex, bool IsFolder, bool Exists);

/// <summary>
/// Looks up favorites' targets (name, icon, folder or file, still there) on a background thread, so
/// a slow or offline network folder never blocks the window. Until a target is looked up it is shown
/// with a plain folder icon and the last part of its path.
/// </summary>
internal static class FavoritesShellInfo
{
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_ATTRIBUTES = 0x000000800;
    private const uint SHGFI_PIDL = 0x000000008;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SFGAO_FOLDER = 0x20000000;
    private const uint SFGAO_STREAM = 0x00400000;

    private static readonly ConcurrentDictionary<string, FavoriteTargetInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<string> Work = new();
    private static SynchronizationContext? _uiContext;
    private static Thread? _worker;
    private static IntPtr _smallImageList;
    private static int _folderIcon = -1;
    private static int _fileIcon = -1;
    private static System.Threading.Timer? _recycleBinTimer;

    /// <summary>Raised on the UI thread when targets were looked up.</summary>
    public static event Action? Updated;

    /// <summary>The system image list of small icons (the one the navigation tree uses).</summary>
    public static IntPtr SmallImageList
    {
        get
        {
            if (_smallImageList == IntPtr.Zero)
            {
                SHFILEINFO info = new();
                _smallImageList = SHGetFileInfo("C:\\", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            }

            return _smallImageList;
        }
    }

    public static int FolderIcon => _folderIcon >= 0 ? _folderIcon : _folderIcon = GenericIcon(FILE_ATTRIBUTE_DIRECTORY, "folder");

    private static int FileIcon => _fileIcon >= 0 ? _fileIcon : _fileIcon = GenericIcon(FILE_ATTRIBUTE_NORMAL, "file");

    private static int GenericIcon(uint attributes, string name)
    {
        SHFILEINFO info = new();
        return SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero ? 0 : info.iIcon;
    }

    /// <summary>What is known about <paramref name="path"/> now; starts a lookup the first time.</summary>
    public static FavoriteTargetInfo Get(string path)
    {
        if (_recycleBinTimer is null && Core.Favorites.SamePath(path, Core.Favorites.RecycleBinPath))
        {
            // The Recycle Bin's icon shows whether it is empty: look it up again now and then
            _recycleBinTimer = new System.Threading.Timer(_ => Queue(Core.Favorites.RecycleBinPath, force: true), null, 3000, 3000);
        }

        if (Cache.TryGetValue(path, out FavoriteTargetInfo? info))
        {
            return info;
        }

        Queue(path);
        string trimmed = path.TrimEnd('\\');
        string name = path.StartsWith("::", StringComparison.Ordinal) ? path : System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            name = path;
        }

        return new FavoriteTargetInfo(name, FolderIcon, IsFolder: true, Exists: true);
    }

    /// <summary>Looks every target up again (after the list changed, or a folder came back).</summary>
    public static void Refresh(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            Queue(path, force: true);
        }
    }

    private static void Queue(string path, bool force = false)
    {
        _uiContext ??= SynchronizationContext.Current;
        if (!Queued.TryAdd(path, 0) && !force)
        {
            return;
        }

        if (_worker is null)
        {
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "Favorites shell info" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        Work.Add(path);
    }

    private static void WorkLoop()
    {
        foreach (string path in Work.GetConsumingEnumerable())
        {
            FavoriteTargetInfo info;
            try
            {
                info = Lookup(path);
            }
            catch
            {
                info = new FavoriteTargetInfo(path, FolderIcon, true, false);
            }

            Queued.TryRemove(path, out _);
            bool changed = !Cache.TryGetValue(path, out FavoriteTargetInfo? old) || old != info;
            Cache[path] = info;
            _pendingNotify |= changed;

            // One repaint per batch of lookups
            if (Work.Count == 0 && _pendingNotify)
            {
                _pendingNotify = false;
                _uiContext?.Post(_ => Updated?.Invoke(), null);
            }
        }
    }

    private static bool _pendingNotify;

    private static FavoriteTargetInfo Lookup(string path)
    {
        SHFILEINFO info = new();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();

        bool isShellPath = path.StartsWith("::", StringComparison.Ordinal) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        bool isServer = path.StartsWith(@"\\", StringComparison.Ordinal) && path.TrimEnd('\\').IndexOf('\\', 2) < 0; // \\computer
        if (!isShellPath && !isServer)
        {
            bool isDirectory = Directory.Exists(path);
            bool isFile = !isDirectory && File.Exists(path);
            if (!isDirectory && !isFile)
            {
                string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                bool looksLikeFile = System.IO.Path.HasExtension(path);
                return new FavoriteTargetInfo(string.IsNullOrEmpty(name) ? path : name,
                    looksLikeFile ? FileIcon : FolderIcon, !looksLikeFile, Exists: false);
            }

            if (SHGetFileInfo(path, 0, ref info, size, SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_DISPLAYNAME) != IntPtr.Zero)
            {
                return new FavoriteTargetInfo(
                    string.IsNullOrEmpty(info.szDisplayName) ? path : info.szDisplayName, info.iIcon, isDirectory, true);
            }

            return new FavoriteTargetInfo(path, isDirectory ? FolderIcon : FileIcon, isDirectory, true);
        }

        // Shell locations (Home, This PC, a library, a network computer…)
        if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
        {
            return new FavoriteTargetInfo(path, FolderIcon, true, false);
        }

        try
        {
            info.dwAttributes = SFGAO_FOLDER | SFGAO_STREAM;
            if (SHGetFileInfo(pidl, 0, ref info, size,
                    SHGFI_PIDL | SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_DISPLAYNAME | SHGFI_ATTRIBUTES) == IntPtr.Zero)
            {
                return new FavoriteTargetInfo(path, FolderIcon, true, true);
            }

            bool folder = (info.dwAttributes & SFGAO_FOLDER) != 0 && (info.dwAttributes & SFGAO_STREAM) == 0;
            return new FavoriteTargetInfo(string.IsNullOrEmpty(info.szDisplayName) ? path : info.szDisplayName, info.iIcon, folder, true);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
}
