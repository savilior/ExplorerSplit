using System.Collections;
using System.Diagnostics;

namespace Explorer2.Core;

/// <summary>
/// The folders pinned to Quick access, in their pinned order. Windows has no API to reorder pins;
/// new pins go to the end, so an item is moved by unpinning the pins from the first changed
/// position on and pinning them again in the new order.
/// </summary>
internal static class QuickAccessPins
{
    private const string QuickAccessNamespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    /// <summary>Raised on the UI thread after Explorer2 changed the pins.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Pinned folders (not frequent folders), in Quick access order.</summary>
    public static List<string> GetPinnedFolders()
    {
        List<string> results = new();
        WithShell(shell =>
        {
            dynamic? folder = shell.NameSpace(QuickAccessNamespace);
            if (folder is null)
            {
                return;
            }

            if (folder.Items() is not IEnumerable items)
            {
                return;
            }

            foreach (dynamic? item in items)
            {
                if (item is null)
                {
                    continue;
                }

                try
                {
                    // Frequent (not pinned) folders report IsPinned = false
                    object? pinned = item.ExtendedProperty("System.Home.IsPinned");
                    string? path = item.Path as string;
                    if (Convert.ToBoolean(item.IsFolder) && pinned is not false && !string.IsNullOrWhiteSpace(path))
                    {
                        results.Add(path!);
                    }
                }
                catch
                {
                    // Skip items without a path
                }
            }
        });

        return results;
    }

    /// <summary>
    /// Puts <paramref name="path"/> at <paramref name="newIndex"/> of the pinned list, pinning it
    /// first if it isn't pinned. Returns false if the pins could not be put in that order.
    /// </summary>
    public static bool MoveTo(string path, int newIndex)
    {
        List<string> current = GetPinnedFolders();
        List<string> target = current.Where(p => !SamePath(p, path)).ToList();
        newIndex = Math.Clamp(newIndex, 0, target.Count);
        target.Insert(newIndex, path);

        int first = 0;
        while (first < current.Count && first < target.Count && SamePath(current[first], target[first]))
        {
            first++;
        }

        if (first == target.Count && current.Count == target.Count)
        {
            return true; // already in that place
        }

        bool ok = true;
        WithShell(shell =>
        {
            // Unpin everything from the first changed position on...
            HashSet<string> toUnpin = new(current.Skip(first), StringComparer.OrdinalIgnoreCase);
            dynamic? quickAccess = shell.NameSpace(QuickAccessNamespace);
            List<dynamic> unpinItems = new();
            if (quickAccess?.Items() is IEnumerable items)
            {
                foreach (dynamic? item in items)
                {
                    if (item?.Path is string itemPath && toUnpin.Contains(itemPath))
                    {
                        unpinItems.Add(item);
                    }
                }
            }

            foreach (dynamic item in unpinItems)
            {
                item.InvokeVerb("unpinfromhome");
            }

            WaitFor(() => GetPinnedFolders().Count <= first);

            // ...and pin them again in the new order (each new pin goes to the end).
            for (int i = first; i < target.Count; i++)
            {
                string pinPath = target[i];
                dynamic? folder = shell.NameSpace(pinPath);
                if (folder?.Self is null)
                {
                    ok = false;
                    continue;
                }

                folder.Self.InvokeVerb("pintohome");
                if (!WaitFor(() => GetPinnedFolders().Any(p => SamePath(p, pinPath))))
                {
                    ok = false;
                }
            }
        });

        List<string> result = GetPinnedFolders();
        List<string> missing = current.Where(p => !result.Any(r => SamePath(r, p))).ToList();
        if (missing.Count > 0)
        {
            // Never lose a pin: pin anything that did not come back (at the end of the list)
            WithShell(shell =>
            {
                foreach (string path2 in missing)
                {
                    shell.NameSpace(path2)?.Self?.InvokeVerb("pintohome");
                }
            });
            return false;
        }

        return ok &&result.Count == target.Count && result.Zip(target).All(pair => SamePath(pair.First, pair.Second));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool WaitFor(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 3000)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void WithShell(Action<dynamic> action)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is not null)
            {
                action(shell);
            }
        }
        catch
        {
            // Shell automation unavailable
        }
        finally
        {
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }
}
