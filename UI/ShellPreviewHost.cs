using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Hosts the registered Windows preview handler (IPreviewHandler) for a file — the same
/// mechanism File Explorer's Preview pane uses for PDF, Word, Excel, PowerPoint, mail, etc.
/// </summary>
internal sealed class ShellPreviewHost : IDisposable
{
    private const string PreviewHandlerShellExKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const int ASSOCSTR_SHELLEXTENSION = 16;
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint STGM_READ = 0x0;
    private const uint STGM_SHARE_DENY_NONE = 0x40;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    private object? _handlerObject;
    private IPreviewHandler? _handler;
    private Control? _hostControl;

    public bool IsLoaded => _handler is not null;

    public static bool HasHandler(string path) => GetHandlerClsid(Path.GetExtension(path)) is not null;

    public bool TryLoad(string path, Control host)
    {
        Unload();

        Guid? clsid = GetHandlerClsid(Path.GetExtension(path));
        if (clsid is null || !host.IsHandleCreated)
        {
            return false;
        }

        object? obj = CreateHandler(clsid.Value);
        if (obj is null)
        {
            return false;
        }

        try
        {
            if (!InitializeHandler(obj, path) || obj is not IPreviewHandler handler)
            {
                ReleaseCom(obj);
                return false;
            }

            NativeMethods.RECT rect = ToRect(host.ClientRectangle);
            if (handler.SetWindow(host.Handle, ref rect) != 0)
            {
                ReleaseCom(obj);
                return false;
            }

            _handlerObject = obj;
            _handler = handler;
            _hostControl = host;

            if (handler.DoPreview() != 0)
            {
                Unload();
                return false;
            }

            return true;
        }
        catch
        {
            _handler = null;
            _handlerObject = null;
            _hostControl = null;
            ReleaseCom(obj);
            return false;
        }
    }

    public void Resize()
    {
        if (_handler is null || _hostControl is null)
        {
            return;
        }

        try
        {
            NativeMethods.RECT rect = ToRect(_hostControl.ClientRectangle);
            _handler.SetRect(ref rect);
        }
        catch
        {
            // Handler process may have exited
        }
    }

    public void Unload()
    {
        IPreviewHandler? handler = _handler;
        object? obj = _handlerObject;
        _handler = null;
        _handlerObject = null;
        _hostControl = null;

        if (handler is not null)
        {
            try
            {
                handler.Unload();
            }
            catch
            {
                // Handler process may have exited
            }
        }

        ReleaseCom(obj);
    }

    public void Dispose() => Unload();

    private static Guid? GetHandlerClsid(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        StringBuilder sb = new(64);
        uint len = (uint)sb.Capacity;
        int hr = AssocQueryString(0, ASSOCSTR_SHELLEXTENSION, extension, PreviewHandlerShellExKey, sb, ref len);
        return hr == 0 && Guid.TryParse(sb.ToString(), out Guid clsid) ? clsid : null;
    }

    private static object? CreateHandler(Guid clsid)
    {
        // Prefer out-of-process activation (prevhost.exe surrogate or the Office servers) so a
        // faulting handler cannot take down Explorer2; fall back to in-proc only if required.
        Guid iid = IidIUnknown;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out object? obj) == 0 && obj is not null)
        {
            return obj;
        }

        iid = IidIUnknown;
        return CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out obj) == 0 ? obj : null;
    }

    private static bool InitializeHandler(object obj, string path)
    {
        try
        {
            if (obj is IInitializeWithFile withFile && withFile.Initialize(path, STGM_READ) == 0)
            {
                return true;
            }
        }
        catch
        {
            // Try the next initialization interface
        }

        try
        {
            if (obj is IInitializeWithItem withItem)
            {
                Guid iidItem = ShellBrowserInterop.IidIShellItem;
                if (ShellBrowserInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out ShellBrowserInterop.IShellItem? item) == 0 &&
                    item is not null)
                {
                    try
                    {
                        if (withItem.Initialize(item, STGM_READ) == 0)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
        }
        catch
        {
            // Try the next initialization interface
        }

        try
        {
            if (obj is IInitializeWithStream withStream &&
                SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, null, out IStream? stream) == 0 &&
                stream is not null)
            {
                // The handler takes its own reference to the stream; release ours either way.
                try
                {
                    return withStream.Initialize(stream, STGM_READ) == 0;
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
            }
        }
        catch
        {
            // No usable initialization interface
        }

        return false;
    }

    private static NativeMethods.RECT ToRect(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try
            {
                Marshal.FinalReleaseComObject(obj);
            }
            catch
            {
                // Ignore
            }
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(
        string pszFile,
        uint grfMode,
        uint dwAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool fCreate,
        IStream? pstmTemplate,
        out IStream? ppstm);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    private interface IPreviewHandler
    {
        [PreserveSig]
        int SetWindow(IntPtr hwnd, ref NativeMethods.RECT prc);

        [PreserveSig]
        int SetRect(ref NativeMethods.RECT prc);

        [PreserveSig]
        int DoPreview();

        [PreserveSig]
        int Unload();

        [PreserveSig]
        int SetFocus();

        [PreserveSig]
        int QueryFocus(out IntPtr phwnd);

        [PreserveSig]
        int TranslateAccelerator(ref NativeMethods.MSG pmsg);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    private interface IInitializeWithFile
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
    private interface IInitializeWithItem
    {
        [PreserveSig]
        int Initialize(ShellBrowserInterop.IShellItem psi, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    private interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(IStream pstream, uint grfMode);
    }
}
