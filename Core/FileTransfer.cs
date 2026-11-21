using System.Runtime.InteropServices;

namespace Explorer2.Core;

/// <summary>
/// Copies or moves items with the Windows shell so the native progress, overwrite/conflict and
/// error dialogs appear, and the operation can be undone with Ctrl+Z. Runs on a background STA
/// thread so the window stays responsive during long transfers.
/// </summary>
internal static class FileTransfer
{
    public enum Operation : uint
    {
        Move = 0x0001,
        Copy = 0x0002
    }

    private const ushort FOF_ALLOWUNDO = 0x0040;

    public static void Start(Operation operation, IReadOnlyList<string> sources, string targetFolder, IntPtr ownerHwnd)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(targetFolder))
        {
            return;
        }

        // SHFileOperation takes double-null-terminated lists; the marshaller appends the final null.
        string from = string.Join("\0", sources) + "\0";
        string to = targetFolder + "\0";

        Thread worker = new(() =>
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    hwnd = ownerHwnd,
                    wFunc = (uint)operation,
                    pFrom = from,
                    pTo = to,
                    fFlags = FOF_ALLOWUNDO
                };

                SHFileOperation(ref op);
            }
            catch
            {
                // The shell reports errors to the user through its own dialogs
            }
        })
        {
            IsBackground = true,
            Name = "Explorer2 file transfer"
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
