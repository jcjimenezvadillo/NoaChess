using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace NoaChess.GUI.Wpf.Theme;

// Makes the title bar follow the Windows theme.
//
// WHY THIS IS NEEDED AT ALL. The title bar is not drawn by the application, it
// is drawn by the desktop window manager, and the DWM paints it light unless the
// window explicitly asks for dark. WPF never asks. So an application whose whole
// client area is dark still gets a white bar across the top, which is exactly
// what every other application on a dark desktop does not do - they all call
// DwmSetWindowAttribute, and this one did not.
//
// THE ATTRIBUTE NUMBER MOVED. Dark mode was attribute 19 in Windows 10 build
// 18985 and earlier, and became 20 afterwards. Both are tried, newest first, and
// a failure on either is ignored: an older Windows simply keeps its light bar,
// which is a cosmetic difference and never a reason to fail a window from
// opening.
//
// It also follows the theme LIVE. Windows broadcasts WM_SETTINGCHANGE with
// "ImmersiveColorSet" when the user flips light/dark, so the bar changes with
// the rest of the desktop instead of staying wrong until the next restart.
public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WmSettingChange = 0x001A;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);

    // Applies the current theme and keeps following it. Safe to call from a
    // constructor: the handle may not exist yet, in which case it waits for it.
    public static void FollowSystemTheme(Window window)
    {
        if (window is null)
            return;

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Attach(window);
        else
            window.SourceInitialized += (_, _) => Attach(window);
    }

    private static void Attach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        Apply(handle, IsSystemDark());

        if (HwndSource.FromHwnd(handle) is not { } source)
            return;

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WmSettingChange && lParam != IntPtr.Zero
                && Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
            {
                Apply(hwnd, IsSystemDark());
            }
            return IntPtr.Zero;
        });
    }

    private static void Apply(IntPtr handle, bool dark)
    {
        int value = dark ? 1 : 0;
        // Newest attribute first; the old one is the fallback for Windows 10
        // builds up to 18985. Return codes are ignored on purpose - an OS that
        // does not know the attribute keeps its default bar.
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref value, sizeof(int));
    }

    // AppsUseLightTheme is the per-application setting, which is the one that
    // governs window chrome; SystemUsesLightTheme covers the taskbar and start
    // menu and is deliberately not read here. A missing key means light, which
    // is the Windows default.
    public static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
