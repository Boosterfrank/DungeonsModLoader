using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace DungeonsModLoader.App.Views.Shell;

/// <summary>
/// Win32 glue for windows that draw their own title bar with <see cref="WindowChrome"/>:
/// <list type="bullet">
///   <item><b>Maximized bounds on multi-monitor / mixed-DPI setups.</b> WM_GETMINMAXINFO reports the work area of the
///   monitor the window is on, and the minimum tracking size from MinWidth/MinHeight at the window's current DPI.
///   Windows 10/11 still place a maximized WS_THICKFRAME window so that its invisible resize frame hangs off every
///   screen edge (the window rect becomes work area + frame, e.g. -8,-8; Explorer behaves the same). Because
///   WindowChrome makes the whole window rect the client area, 8px of content would be cut off on each side, so while
///   maximized WM_NCCALCSIZE insets the client area to exactly the monitor's work area (leaving a sliver for an
///   auto-hide taskbar). Content then fills the work area and the taskbar stays visible.</item>
///   <item><b>Hit testing while maximized.</b> WindowChrome's own WM_NCHITTEST assumes client == window rect, which is
///   no longer true with the inset client, so the helper answers the hit test itself in that state: hit-test-visible
///   chrome elements (caption buttons) are client, the caption band drags, there are no resize borders.</item>
///   <item><b>Windows 11 snap layouts.</b> The custom maximize button reports itself as HTMAXBUTTON so the system
///   shows the snap-layouts flyout on hover. The system then owns the mouse over that button, so hover and pressed
///   visuals are mirrored through the <see cref="IsChromeHoveredProperty"/> / <see cref="IsChromePressedProperty"/>
///   attached properties and the click is dispatched here (WM_NCLBUTTONDOWN/UP).</item>
/// </list>
/// Usage: call <see cref="Attach"/> from <c>OnSourceInitialized</c> (the HWND must exist). The helper detaches itself
/// when the window closes. Hooks added later run first, so this runs ahead of WindowChrome's own hook.
/// </summary>
public sealed class WindowChromeHelper : IDisposable
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_NCMOUSELEAVE = 0x02A2;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTMAXBUTTON = 9;
    private const int WVR_REDRAW = 0x0300;

    private const uint MONITOR_DEFAULTTONEAREST = 0x0002;
    private const uint TME_LEAVE = 0x0002;
    private const uint TME_NONCLIENT = 0x0010;
    private const uint ABM_GETAUTOHIDEBAREX = 0x0000000B;
    private const uint ABS_AUTOHIDE = 0x0001;
    private const uint ABM_GETSTATE = 0x00000004;

    /// <summary>Gap left on a screen edge that hosts an auto-hide taskbar so it can still be summoned.</summary>
    private const int AutoHideTaskbarGap = 2;

    private readonly Window _window;
    private readonly FrameworkElement? _maximizeButton;
    private HwndSource? _source;
    private bool _disposed;

    private WindowChromeHelper(Window window, FrameworkElement? maximizeButton)
    {
        _window = window;
        _maximizeButton = maximizeButton;
    }

    /// <summary>
    /// Set by the helper on the maximize button while the system reports the mouse over it (non-client hover).
    /// Styles should treat it like <c>IsMouseOver</c>.
    /// </summary>
    public static readonly DependencyProperty IsChromeHoveredProperty = DependencyProperty.RegisterAttached(
        "IsChromeHovered", typeof(bool), typeof(WindowChromeHelper), new PropertyMetadata(false));

    /// <summary>Set by the helper on the maximize button while the left button is held down over it.</summary>
    public static readonly DependencyProperty IsChromePressedProperty = DependencyProperty.RegisterAttached(
        "IsChromePressed", typeof(bool), typeof(WindowChromeHelper), new PropertyMetadata(false));

    public static bool GetIsChromeHovered(DependencyObject element) => (bool)element.GetValue(IsChromeHoveredProperty);
    public static void SetIsChromeHovered(DependencyObject element, bool value) => element.SetValue(IsChromeHoveredProperty, value);
    public static bool GetIsChromePressed(DependencyObject element) => (bool)element.GetValue(IsChromePressedProperty);
    public static void SetIsChromePressed(DependencyObject element, bool value) => element.SetValue(IsChromePressedProperty, value);

    /// <summary>
    /// Hooks the window's message loop. Must be called once the HWND exists (e.g. from <c>OnSourceInitialized</c>).
    /// </summary>
    /// <param name="window">The window that uses a custom <see cref="WindowChrome"/>.</param>
    /// <param name="maximizeButton">
    /// The custom maximize/restore button, or null to skip snap-layouts support. When given, the button no longer
    /// receives WPF mouse input (the system owns it); its hover/pressed visuals come from the attached properties.
    /// </param>
    public static WindowChromeHelper Attach(Window window, FrameworkElement? maximizeButton = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var source = (HwndSource?)PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The window handle has not been created yet. Call Attach from OnSourceInitialized.");

        var helper = new WindowChromeHelper(window, maximizeButton) { _source = source };
        source.AddHook(helper.WndProc);
        window.Closed += helper.OnWindowClosed;
        return helper;
    }

    /// <summary>Toggles between maximized and normal (what a click on the maximize button does).</summary>
    public static void ToggleMaximize(Window window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Closed -= OnWindowClosed;
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                // Not marked handled on purpose: WPF's Window still runs afterwards and only rewrites the tracking
                // sizes (honouring Min/Max Width/Height), while the maximized position/size set here survive.
                ApplyMinMaxInfo(hwnd, lParam);
                break;

            case WM_NCCALCSIZE:
                if (IsZoomed(hwnd))
                {
                    InsetMaximizedClientArea(lParam);
                    handled = true;
                    return wParam != IntPtr.Zero ? WVR_REDRAW : IntPtr.Zero;
                }
                break;

            case WM_NCHITTEST:
                if (_maximizeButton is not null && IsOverElement(_maximizeButton, lParam))
                {
                    handled = true;
                    return HTMAXBUTTON;
                }

                if (IsZoomed(hwnd))
                {
                    handled = true;
                    return HitTestMaximized(lParam);
                }
                break;

            case WM_NCMOUSEMOVE:
                if (_maximizeButton is not null)
                {
                    var over = wParam == HTMAXBUTTON;
                    SetHover(over, hwnd);
                    if (over)
                    {
                        // Keep DefWindowProc from hot-tracking a (non-existent) system caption button.
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                break;

            case WM_NCMOUSELEAVE:
            case WM_MOUSEMOVE:
                if (_maximizeButton is not null)
                {
                    SetHover(false, hwnd);
                }
                break;

            case WM_NCLBUTTONDOWN:
            case WM_NCLBUTTONDBLCLK:
                if (_maximizeButton is not null && wParam == HTMAXBUTTON)
                {
                    SetIsChromePressed(_maximizeButton, true);
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            case WM_NCLBUTTONUP:
                if (_maximizeButton is not null)
                {
                    var wasPressed = GetIsChromePressed(_maximizeButton);
                    SetIsChromePressed(_maximizeButton, false);
                    if (wParam == HTMAXBUTTON)
                    {
                        if (wasPressed)
                        {
                            SetHover(false, hwnd);
                            ToggleMaximize(_window);
                        }

                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                break;

            case WM_LBUTTONUP:
                if (_maximizeButton is not null)
                {
                    SetIsChromePressed(_maximizeButton, false);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void ApplyMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (TryGetMonitorInfo(monitor, out var info))
        {
            // Maximized position is relative to the monitor's origin; size is the work area (excludes the taskbar).
            var work = info.rcWork;
            var screen = info.rcMonitor;
            mmi.ptMaxPosition.X = work.Left - screen.Left;
            mmi.ptMaxPosition.Y = work.Top - screen.Top;
            mmi.ptMaxSize.X = work.Right - work.Left;
            mmi.ptMaxSize.Y = work.Bottom - work.Top;
        }

        // Min tracking size in device pixels at the window's current DPI (per-monitor V2 aware).
        var dpi = VisualTreeHelper.GetDpi(_window);
        if (!double.IsNaN(_window.MinWidth) && _window.MinWidth > 0)
        {
            mmi.ptMinTrackSize.X = (int)Math.Ceiling(_window.MinWidth * dpi.DpiScaleX);
        }

        if (!double.IsNaN(_window.MinHeight) && _window.MinHeight > 0)
        {
            mmi.ptMinTrackSize.Y = (int)Math.Ceiling(_window.MinHeight * dpi.DpiScaleY);
        }

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
    }

    /// <summary>
    /// WM_NCCALCSIZE while maximized: lParam starts as the proposed window rect (the first field is a RECT for both
    /// wParam values) and must end as the client rect. Clip it to the monitor's work area.
    /// </summary>
    private static void InsetMaximizedClientArea(IntPtr lParam)
    {
        var rc = Marshal.PtrToStructure<RECT>(lParam);
        var monitor = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var work = info.rcWork;
        rc.Left = Math.Max(rc.Left, work.Left);
        rc.Top = Math.Max(rc.Top, work.Top);
        rc.Right = Math.Min(rc.Right, work.Right);
        rc.Bottom = Math.Min(rc.Bottom, work.Bottom);

        // An auto-hide taskbar sits on top of the work area; leave it a sliver to be summoned from.
        if (IsTaskbarAutoHide())
        {
            var screen = info.rcMonitor;
            if (HasAutoHideBar(ABE_LEFT, screen)) { rc.Left += AutoHideTaskbarGap; }
            if (HasAutoHideBar(ABE_TOP, screen)) { rc.Top += AutoHideTaskbarGap; }
            if (HasAutoHideBar(ABE_RIGHT, screen)) { rc.Right -= AutoHideTaskbarGap; }
            if (HasAutoHideBar(ABE_BOTTOM, screen)) { rc.Bottom -= AutoHideTaskbarGap; }
        }

        Marshal.StructureToPtr(rc, lParam, fDeleteOld: false);
    }

    /// <summary>WM_NCHITTEST while maximized: chrome-visible elements are client, the caption band drags, no borders.</summary>
    private IntPtr HitTestMaximized(IntPtr lParam)
    {
        if (!TryPointFromScreen(_window, GetScreenPoint(lParam), out var local))
        {
            return HTCLIENT;
        }

        if (_window.InputHitTest(local) is { } element && WindowChrome.GetIsHitTestVisibleInChrome(element))
        {
            return HTCLIENT;
        }

        var captionHeight = WindowChrome.GetWindowChrome(_window)?.CaptionHeight ?? 0;
        return local.Y >= 0 && local.Y < captionHeight ? HTCAPTION : HTCLIENT;
    }

    private static bool IsOverElement(FrameworkElement element, IntPtr lParam)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        return TryPointFromScreen(element, GetScreenPoint(lParam), out var local)
            && new Rect(0, 0, element.ActualWidth, element.ActualHeight).Contains(local);
    }

    private static bool TryPointFromScreen(Visual visual, Point screen, out Point local)
    {
        try
        {
            local = visual.PointFromScreen(screen);
            return true;
        }
        catch (InvalidOperationException)
        {
            local = default; // Not connected to a PresentationSource (being torn down).
            return false;
        }
    }

    /// <summary>lParam packs signed 16-bit screen coordinates (negative on monitors left of / above the primary).</summary>
    private static Point GetScreenPoint(IntPtr lParam)
    {
        var raw = unchecked((int)(long)lParam);
        return new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
    }

    private void SetHover(bool hovered, IntPtr hwnd)
    {
        if (_maximizeButton is null || GetIsChromeHovered(_maximizeButton) == hovered)
        {
            return;
        }

        SetIsChromeHovered(_maximizeButton, hovered);
        if (hovered)
        {
            // Ask for WM_NCMOUSELEAVE so the hover state is cleared when the mouse leaves the window entirely.
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE | TME_NONCLIENT,
                hwndTrack = hwnd,
                dwHoverTime = 0,
            };
            _ = TrackMouseEvent(ref tme);
        }
        else
        {
            SetIsChromePressed(_maximizeButton, false);
        }
    }

    private static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFO info)
    {
        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static bool IsTaskbarAutoHide()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return ((uint)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    private static bool HasAutoHideBar(uint edge, RECT monitor)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), uEdge = edge, rc = monitor };
        return SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data) != IntPtr.Zero;
    }

    #region Win32

    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    #endregion
}
