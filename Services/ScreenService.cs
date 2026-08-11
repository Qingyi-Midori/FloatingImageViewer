using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatingImageViewer.Services;

/// <summary>获取窗口所在显示器的可用工作区（屏幕边缘吸附用）。</summary>
public static class ScreenService
{
    public static Rect GetWorkArea(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(monitor, ref info))
                {
                    var dpi = VisualTreeHelper.GetDpi(window);
                    return new Rect(
                        info.rcWork.Left / dpi.DpiScaleX,
                        info.rcWork.Top / dpi.DpiScaleY,
                        (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX,
                        (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY);
                }
            }
        }
        catch
        {
        }

        var workArea = SystemParameters.WorkArea;
        return new Rect(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
    }

    /// <summary>枚举其他可见顶层窗口的屏幕矩形（DIPs），用于“贴住其他软件边缘”。</summary>
    public static List<Rect> GetVisibleWindowRects(IntPtr excludeHwnd, double dpiScaleX, double dpiScaleY)
    {
        var rects = new List<Rect>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHwnd || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top)
            {
                return true;
            }

            rects.Add(new Rect(
                rc.Left / dpiScaleX,
                rc.Top / dpiScaleY,
                (rc.Right - rc.Left) / dpiScaleX,
                (rc.Bottom - rc.Top) / dpiScaleY));
            return true;
        }, IntPtr.Zero);
        return rects;
    }

    /// <summary>
    /// 一次性完成“移动 + 缩放”，避免 WPF 分多次更新 HWND 导致透明窗口闪烁。
    /// 窗口未显示时回退为直接设置属性（便于无窗口环境下的测试）。
    /// </summary>
    public static void MoveResize(Window window, double left, double top, double width, double height)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.IsVisible)
        {
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        int x = (int)Math.Round(left * dpi.DpiScaleX);
        int y = (int)Math.Round(top * dpi.DpiScaleY);
        int w = (int)Math.Round(width * dpi.DpiScaleX);
        int h = (int)Math.Round(height * dpi.DpiScaleY);
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            w,
            h,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS);
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOCOPYBITS = 0x0100;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
