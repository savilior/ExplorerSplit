using System.IO.Pipes;
using Explorer2.Configuration;
using Explorer2.UI;

namespace Explorer2;

internal static class Program
{
    internal const string CoordinatorMutexPrefix = @"Local\Explorer2_Coordinator_Mutex";
    internal const string TourArgument = "--tour";
    private static readonly string CoordinatorMutexName = CoordinatorMutexPrefix + AppPaths.InstanceSuffix;

    [STAThread]
    private static void Main(string[] args)
    {
        // --take-tour: what "Take the tour" does (sample folders, then the demo Explorer2 that plays it)
        if (args.Contains("--take-tour", StringComparer.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            TourLauncher.Launch();
            return;
        }

        // --standalone: one window, no tray and no hand-off to a running Explorer2. Lets a test
        // build run next to the installed copy without touching it.
        bool standalone = args.Contains("--standalone", StringComparer.OrdinalIgnoreCase);
        // --tour: play the guided tour. Only in demo mode, since the tour changes Favorites and settings.
        bool tour = AppPaths.IsDemo && args.Contains(TourArgument, StringComparer.OrdinalIgnoreCase);
        args = args.Where(a => !a.Equals("--standalone", StringComparison.OrdinalIgnoreCase) &&
                               !a.Equals(TourArgument, StringComparison.OrdinalIgnoreCase)).ToArray();

        string? startupFolder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : null;

        using Mutex coordinatorMutex = new(initiallyOwned: !standalone, CoordinatorMutexName, out bool createdMutex);
        bool isCoordinator = createdMutex && !standalone;

        if (!isCoordinator && !standalone)
        {
            if (TryForwardOpenWindowToCoordinator(startupFolder))
            {
                return;
            }
        }

        AppTheme.Mode = AppSettings.Load().Theme;
        AppTheme.ApplyToProcess();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Right-click on the taskbar button: Favorites and recent folders (the installed copy's list,
        // so not in demo mode)
        if (!AppPaths.IsDemo)
        {
            TaskbarJumpList.Start(() => Core.FolderHistory.Paths);
            Core.FolderHistory.Changed += TaskbarJumpList.Schedule;
        }

        Application.ApplicationExit += (_, _) => Core.FolderHistory.Save();

        if (isCoordinator)
        {
            if (SynchronizationContext.Current is null)
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            }

            using TrayApplicationContext context = new(startupFolder);
            if (tour)
            {
                GuidedTour.Start(context);
            }

            Application.Run(context);
        }
        else
        {
            AppSettings settings = AppSettings.Load();
            using DualPaneExplorerForm standaloneForm = new(
                startupFolder,
                startupFolder,
                startInSplit: settings.DefaultStartInSplitMode);
            Application.Run(standaloneForm);
        }
    }

    private static bool TryForwardOpenWindowToCoordinator(string? folderPath)
    {
        try
        {
            using NamedPipeClientStream client = new(".", TrayApplicationContext.IpcPipeName, PipeDirection.Out);
            client.Connect(1500);
            using StreamWriter writer = new(client);
            writer.WriteLine(folderPath ?? string.Empty);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
