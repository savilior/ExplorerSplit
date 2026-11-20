using System.IO.Pipes;
using ExplorerSplit.Configuration;
using ExplorerSplit.UI;

namespace ExplorerSplit;

internal static class Program
{
    private const string CoordinatorMutexName = @"Local\ExplorerSplit_Coordinator_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        string? startupFolder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : null;

        using Mutex coordinatorMutex = new(initiallyOwned: true, CoordinatorMutexName, out bool isCoordinator);

        if (!isCoordinator)
        {
            if (TryForwardOpenWindowToCoordinator(startupFolder))
            {
                return;
            }
        }

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
