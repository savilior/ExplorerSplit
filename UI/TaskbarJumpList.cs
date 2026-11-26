using System.Runtime.InteropServices;
using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// The list shown when right-clicking Explorer2 on the taskbar, like File Explorer's pinned folders:
/// the Favorites (top-level folders under "Favorites", each top-level group as its own heading)
/// and the recently opened folders. Each entry opens the folder in a new Explorer2 window. Rebuilt
/// (after a short pause) whenever the Favorites or the recent folders change. Entries the user
/// removes from the list ("Remove from this list") are left out until they are added again.
/// </summary>
internal static class TaskbarJumpList
{
    private const int MaxPerCategory = 12;
    private const int MaxRecent = 10;

    private static System.Windows.Forms.Timer? _timer;
    private static Func<IReadOnlyList<string>>? _recentFolders;

    /// <summary>Starts keeping the list up to date (call once, on the UI thread).</summary>
    public static void Start(Func<IReadOnlyList<string>> recentFolders)
    {
        if (_timer is not null)
        {
            return;
        }

        _recentFolders = recentFolders;
        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Build();
        };
        Favorites.Changed += Schedule;
        FavoritesShellInfo.Updated += Schedule; // names and icons looked up after the first build
        Schedule();
    }

    /// <summary>Rebuilds the list after a short pause (several changes make one rebuild).</summary>
    public static void Schedule()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    private static void Build()
    {
        ICustomDestinationList? list = null;
        try
        {
            list = (ICustomDestinationList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6"))!)!;
            Guid iidArray = typeof(IObjectArray).GUID;
            list.BeginList(out uint slots, ref iidArray, out object removedObj);
            HashSet<string> removed = RemovedArguments(removedObj as IObjectArray);
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;

            List<(string Title, List<Entry> Entries)> categories = new();
            if (Configuration.AppSettings.FavoritesSectionEnabled)
            {
                categories.Add(("Favorites", Collect(Favorites.Roots.Where(n => !n.IsGroup), removed, used)));
                foreach (FavoriteNode group in Favorites.Roots.Where(n => n.IsGroup))
                {
                    categories.Add((string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name!,
                        Collect(Favorites.Flatten(group.Children!).Where(n => !n.IsGroup), removed, used)));
                }
            }

            IEnumerable<FavoriteNode> recent = (_recentFolders?.Invoke() ?? Array.Empty<string>())
                .Take(MaxRecent).Select(FavoriteNode.Shortcut);
            List<Entry> recentEntries = Collect(recent, removed, used);

            // The taskbar shows only as many items as its slot count (10 unless changed in the
            // registry) and drops the categories past it. Share the slots so every heading shows:
            // a few for Recent, the rest taken in turn by the Favorites headings.
            int budget = Math.Max((int)slots, 1);
            int recentShare = Math.Min(recentEntries.Count, categories.Count > 0 ? Math.Max(2, budget / 4) : budget);
            int[] shares = new int[categories.Count];
            int left = budget - recentShare;
            for (bool progress = true; left > 0 && progress;)
            {
                progress = false;
                for (int i = 0; i < categories.Count && left > 0; i++)
                {
                    if (shares[i] < categories[i].Entries.Count)
                    {
                        shares[i]++;
                        left--;
                        progress = true;
                    }
                }
            }

            recentShare = Math.Min(recentEntries.Count, recentShare + left);
            for (int i = 0; i < categories.Count; i++)
            {
                AppendCategory(list, categories[i].Title, categories[i].Entries.Take(shares[i]), exe);
            }

            AppendCategory(list, "Recent", recentEntries.Take(recentShare), exe);
            list.CommitList();
        }
        catch
        {
            // The taskbar keeps its previous list
            try
            {
                list?.AbortList();
            }
            catch
            {
                // Nothing more to do
            }
        }
        finally
        {
            if (list is not null)
            {
                Marshal.ReleaseComObject(list);
            }
        }
    }

    private readonly record struct Entry(string Path, string Arguments, string Name);

    /// <summary>The nodes that can go in the list: existing folders, not removed by the user, not already listed.</summary>
    private static List<Entry> Collect(IEnumerable<FavoriteNode> nodes, HashSet<string> removed, HashSet<string> used)
    {
        List<Entry> result = new();
        foreach (FavoriteNode node in nodes)
        {
            string path = node.Path!;
            string arguments = Quote(path);
            FavoriteTargetInfo info = FavoritesShellInfo.Get(path);
            if (result.Count >= MaxPerCategory || !info.IsFolder || !info.Exists || removed.Contains(arguments) || used.Contains(path))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(node.Name) ? info.DisplayName : node.Name!;
            if (name.StartsWith("::", StringComparison.Ordinal))
            {
                continue; // not looked up yet: shown on the next rebuild
            }

            used.Add(path);
            result.Add(new Entry(path, arguments, name));
        }

        return result;
    }

    private static void AppendCategory(ICustomDestinationList list, string title, IEnumerable<Entry> entries, string exe)
    {
        IObjectCollection items = (IObjectCollection)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"))!)!;
        try
        {
            int count = 0;
            foreach (Entry entry in entries)
            {
                items.AddObject(CreateLink(exe, entry.Arguments, entry.Name, entry.Path));
                count++;
            }

            if (count > 0)
            {
                list.AppendCategory(title, (IObjectArray)items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(items);
        }
    }

    // A backslash before the closing quote would escape it: double it ("C:\\" reads as C:\)
    private static string Quote(string path) => "\"" + (path.EndsWith('\\') ? path + "\\" : path) + "\"";

    private static IShellLinkW CreateLink(string exe, string arguments, string title, string path)
    {
        IShellLinkW link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetDescription(path);
        (string iconFile, int iconIndex) = IconLocation(path);
        link.SetIconLocation(iconFile, iconIndex);

        IPropertyStore store = (IPropertyStore)link;
        PROPERTYKEY titleKey = new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };
        PROPVARIANT value = new() { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            store.SetValue(ref titleKey, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.p);
        }

        return link;
    }

    /// <summary>The folder's own icon (a custom folder icon, This PC, the Recycle Bin…).</summary>
    private static (string File, int Index) IconLocation(string path)
    {
        const uint SHGFI_ICONLOCATION = 0x1000;
        const uint SHGFI_PIDL = 0x8;
        SHFILEINFO info = new();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        try
        {
            if (path.StartsWith("::", StringComparison.Ordinal) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                {
                    try
                    {
                        SHGetFileInfo(pidl, 0, ref info, size, SHGFI_PIDL | SHGFI_ICONLOCATION);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }
            }
            else
            {
                SHGetFileInfo(path, 0, ref info, size, SHGFI_ICONLOCATION);
            }
        }
        catch
        {
            // Plain folder icon below
        }

        return string.IsNullOrEmpty(info.szDisplayName)
            ? (Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\imageres.dll"), 3)
            : (info.szDisplayName, info.iIcon);
    }

    private static HashSet<string> RemovedArguments(IObjectArray? removedItems)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (removedItems is null)
        {
            return result;
        }

        try
        {
            removedItems.GetCount(out uint count);
            Guid iidLink = typeof(IShellLinkW).GUID;
            for (uint i = 0; i < count; i++)
            {
                removedItems.GetAt(i, ref iidLink, out object obj);
                if (obj is IShellLinkW link)
                {
                    System.Text.StringBuilder arguments = new(1024);
                    link.GetArguments(arguments, arguments.Capacity);
                    result.Add(arguments.ToString());
                    Marshal.ReleaseComObject(link);
                }
            }
        }
        catch
        {
            // Treat as nothing removed
        }
        finally
        {
            Marshal.ReleaseComObject(removedItems);
        }

        return result;
    }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        void AbortList();
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr p;
        public IntPtr p2;
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
