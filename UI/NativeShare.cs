using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Explorer2.UI;

/// <summary>
/// Shows the native Windows share sheet (the same one File Explorer's Share button opens)
/// for a set of files, via DataTransferManager bound to a top-level desktop window.
/// </summary>
internal static class NativeShare
{
    private static readonly Dictionary<IntPtr, DataTransferManager> Managers = new();
    private static readonly Dictionary<IntPtr, IReadOnlyList<string>> PendingPaths = new();

    public static bool TryShowShareSheet(IntPtr topLevelHwnd, IReadOnlyList<string> paths)
    {
        if (topLevelHwnd == IntPtr.Zero || paths.Count == 0)
        {
            return false;
        }

        try
        {
            if (!Managers.TryGetValue(topLevelHwnd, out DataTransferManager? manager))
            {
                manager = DataTransferManagerInterop.GetForWindow(topLevelHwnd);
                IntPtr hwnd = topLevelHwnd;
                manager.DataRequested += (_, args) => OnDataRequested(hwnd, args);
                Managers[topLevelHwnd] = manager;
            }

            PendingPaths[topLevelHwnd] = paths.ToList();
            DataTransferManagerInterop.ShowShareUIForWindow(topLevelHwnd);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Drops the cached manager for a window that is closing.</summary>
    public static void Forget(IntPtr topLevelHwnd)
    {
        Managers.Remove(topLevelHwnd);
        PendingPaths.Remove(topLevelHwnd);
    }

    private static async void OnDataRequested(IntPtr hwnd, DataRequestedEventArgs args)
    {
        DataRequest request = args.Request;
        if (!PendingPaths.TryGetValue(hwnd, out IReadOnlyList<string>? paths) || paths.Count == 0)
        {
            request.FailWithDisplayText("Nothing selected to share.");
            return;
        }

        DataRequestDeferral deferral = request.GetDeferral();
        try
        {
            List<IStorageItem> items = new();
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    }
                    else if (File.Exists(path))
                    {
                        items.Add(await StorageFile.GetFileFromPathAsync(path));
                    }
                }
                catch
                {
                    // Skip items the storage broker cannot open
                }
            }

            if (items.Count == 0)
            {
                request.FailWithDisplayText("The selected items can't be shared.");
                return;
            }

            request.Data.Properties.Title = items.Count == 1
                ? items[0].Name
                : $"{items.Count} items";
            request.Data.SetStorageItems(items);
        }
        catch
        {
            request.FailWithDisplayText("The selected items can't be shared.");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
