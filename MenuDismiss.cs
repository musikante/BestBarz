using System.Runtime.InteropServices;

namespace BestBarz;

/// <summary>
/// Closes a context menu on Esc, on a click outside it, or when another app's window comes to
/// the foreground. Needed because bars and popups are WS_EX_NOACTIVATE: the app never becomes
/// active, so WinForms' own dismissal never fires. Hooks live only while the menu is open.
/// </summary>
internal static class MenuDismiss
{
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    public static void Attach(ContextMenuStrip menu)
    {
        IntPtr kbHook = IntPtr.Zero, mouseHook = IntPtr.Zero, fgHook = IntPtr.Zero;
        Win32.LowLevelKeyboardProc? kbProc = null, mouseProc = null;
        Win32.WinEventProc? fgProc = null;

        void CloseMenu() => menu.BeginInvoke(() => menu.Close(ToolStripDropDownCloseReason.AppFocusChange));

        void Unhook()
        {
            if (kbHook    != IntPtr.Zero) Win32.UnhookWindowsHookEx(kbHook);
            if (mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(mouseHook);
            if (fgHook    != IntPtr.Zero) Win32.UnhookWinEvent(fgHook);
            kbHook = mouseHook = fgHook = IntPtr.Zero;
            kbProc = mouseProc = null;
            fgProc = null;
        }

        menu.Opened += (_, _) =>
        {
            Unhook();
            var module = Win32.GetModuleHandle(null);

            kbProc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0 && wParam == (IntPtr)Win32.WM_KEYDOWN &&
                    Marshal.ReadInt32(lParam) == (int)Keys.Escape)
                {
                    menu.BeginInvoke(() => menu.Close(ToolStripDropDownCloseReason.Keyboard));
                    return 1;
                }
                return Win32.CallNextHookEx(kbHook, nCode, wParam, lParam);
            };

            mouseProc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0 && (int)wParam is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN
                                              or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
                {
                    var pt = new Point(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));
                    if (!menu.Bounds.Contains(pt)) CloseMenu();
                }
                return Win32.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            };

            fgProc = (_, _, hwnd, _, _, _, _) =>
            {
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != OwnPid) CloseMenu();
            };

            kbHook    = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, kbProc,    module, 0);
            mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL,    mouseProc, module, 0);
            fgHook    = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
                                              IntPtr.Zero, fgProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        };
        menu.Closed   += (_, _) => Unhook();
        menu.Disposed += (_, _) => Unhook();
    }
}
