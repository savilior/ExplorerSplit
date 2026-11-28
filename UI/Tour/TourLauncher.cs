using System.Diagnostics;
using Explorer2.Configuration;

namespace Explorer2.UI;

/// <summary>
/// "Take the tour": starts a separate demo Explorer2 (its own data in %TEMP%\Explorer2Tour) that
/// plays the guided tour on sample folders. The installed Explorer2 and its data are not touched.
/// </summary>
internal static class TourLauncher
{
    public static void Launch()
    {
        if (Mutex.TryOpenExisting(Program.CoordinatorMutexPrefix + AppPaths.DemoSuffix, out Mutex? running))
        {
            running.Dispose();
            MessageBox.Show("The tour is already open (or another demo Explorer2 is running).", "Explorer2",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            TourSamples.CreateFresh();
            ProcessStartInfo start = new(Environment.ProcessPath ?? Application.ExecutablePath)
            {
                UseShellExecute = false,
                ArgumentList = { Program.TourArgument, TourSamples.Projects }
            };
            start.Environment["EXPLORER2_DATA_DIR"] = TourSamples.Data;
            Process.Start(start);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The tour could not start:\n{ex.Message}", "Explorer2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
