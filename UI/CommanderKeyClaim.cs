using System.Diagnostics;
using System.Runtime.InteropServices;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Claims plain F6 for Explorer2 windows in split mode before other programs see it.
/// Some tools register F6 system-wide (e.g. LangOver's "Reverse text" hotkey), which would
/// otherwise swallow Total Commander's F6 Move. A low-level keyboard hook installed after those
/// tools is called first; it only takes F6 while an Explorer2 window in split mode is the
/// foreground window and no text box has focus — everywhere else F6 passes through untouched.
/// </summary>
internal static class CommanderKeyClaim
{
    private const int VkF6 = 0x75;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private static readonly List<DualPaneExplorerForm> Forms = new();
    private static readonly NativeMethods.LowLevelKeyboardProc HookProc = OnKeyboardHook;
    private static IntPtr s_hook = IntPtr.Zero;

    public static void Register(DualPaneExplorerForm form)
    {
        if (!Forms.Contains(form))
        {
            Forms.Add(form);
        }

        if (s_hook == IntPtr.Zero)
        {
            using Process process = Process.GetCurrentProcess();
            using ProcessModule? module = process.MainModule;
            s_hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                HookProc,
                NativeMethods.GetModuleHandle(module?.ModuleName),
                0);
        }
    }

    public static void Unregister(DualPaneExplorerForm form)
    {
        Forms.Remove(form);
        if (Forms.Count == 0 && s_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(s_hook);
            s_hook = IntPtr.Zero;
        }
    }

    private static IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            NativeMethods.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int message = wParam.ToInt32();
            bool isDown = message is NativeMethods.WM_KEYDOWN;
            bool isUp = message is NativeMethods.WM_KEYUP;

            if (info.vkCode == VkF6 && (isDown || isUp) && !IsModifierDown())
            {
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                DualPaneExplorerForm? form = Forms.FirstOrDefault(f => f.IsHandleCreated && !f.IsDisposed && f.Handle == foreground);
                if (form is not null && form.WantsClaimedF6())
                {
                    if (isDown)
                    {
                        form.BeginInvoke(form.OnClaimedF6);
                    }

                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(s_hook, nCode, wParam, lParam);
    }

    private static bool IsModifierDown() =>
        (NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0;
}
