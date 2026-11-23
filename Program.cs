using System.IO.Pipes;
using Explorer2.Configuration;
using Explorer2.UI;

namespace Explorer2;

internal static class Program
{
    private const string CoordinatorMutexName = @"Local\Explorer2_Coordinator_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        // --standalone: one window, no tray and no hand-off to a running Explorer2. Lets a test
        // build run next to the installed copy without touching it.
        bool standalone = args.Contains("--standalone", StringComparer.OrdinalIgnoreCase);
        args = args.Where(a => !a.Equals("--standalone", StringComparison.OrdinalIgnoreCase)).ToArray();

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

        AppTheme.ApplyToProcess();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (isCoordinator)
        {
            if (SynchronizationContext.Current is null)
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            }

            using TrayApplicationContext context = new(startupFolder);
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
