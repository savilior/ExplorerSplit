using System.Diagnostics;
using ExplorerSplit.UI;

namespace ExplorerSplit;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\ExplorerSplit_SingleInstance_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        TerminatePreviousInstances();

        using Mutex singleInstanceMutex = new(initiallyOwned: true, SingleInstanceMutexName, out _);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string? startupFolder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : null;

        using TrayApplicationContext context = new(startupFolder);
        Application.Run(context);
    }

    private static void TerminatePreviousInstances()
    {
        int currentPid = Environment.ProcessId;
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id != currentPid &&
                    (proc.ProcessName.Equals("ExplorerSplit", StringComparison.OrdinalIgnoreCase) ||
                     proc.ProcessName.StartsWith("old_ExplorerSplit", StringComparison.OrdinalIgnoreCase)))
                {
                    proc.Kill();
                    proc.WaitForExit(1500);
                }
            }
            catch
            {
                // Ignore processes that cannot be accessed or have already exited
            }
            finally
            {
                proc.Dispose();
            }
        }
    }
}
