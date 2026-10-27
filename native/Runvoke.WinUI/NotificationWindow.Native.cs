using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Runvoke.WinUI;

internal static class NativeWindowStyles
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const long WsSysMenu = 0x00080000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExAppWindow = 0x00040000;
    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlWndProc = -4;
    private const uint WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    public static void MakeNotificationWindow(IntPtr hwnd)
    {
        var windowStyle = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        windowStyle &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(windowStyle));
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate;
        style &= ~WsExAppWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0020);
    }

    public static IDisposable InstallTransparentHitTest(IntPtr hwnd, Func<int, int, bool> isInside)
    {
        var hook = new HitTestHook(hwnd, isInside);
        hook.Install();
        return hook;
    }

    public static void SetNoActivate(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoActivate | 0x0001 | 0x0002);

    public static void Position(IntPtr hwnd, int width, int height, string position, IntPtr displayHwnd = default)
    {
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(displayHwnd == IntPtr.Zero ? hwnd : displayHwnd);
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        const int margin = 18;
        var x = position.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? work.X + margin
            : position.Contains("center", StringComparison.OrdinalIgnoreCase)
                ? work.X + (work.Width - width) / 2
                : work.X + work.Width - width - margin;
        var y = position.Contains("top", StringComparison.OrdinalIgnoreCase) ? work.Y + margin : work.Y + work.Height - height - margin;
        SetWindowPos(hwnd, HwndTopMost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class HitTestHook : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Func<int, int, bool> _isInside;
        private readonly NativeWndProc _callback;
        private IntPtr _previous;
        private bool _installed;

        public HitTestHook(IntPtr hwnd, Func<int, int, bool> isInside)
        {
            _hwnd = hwnd;
            _isInside = isInside;
            _callback = WndProc;
        }

        public void Install()
        {
            if (_installed) return;
            _previous = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_callback));
            _installed = _previous != IntPtr.Zero;
        }

        private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmNcHitTest)
            {
                var point = new NativePoint((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
                if (ScreenToClient(hwnd, ref point) && !_isInside(point.X, point.Y)) return HtTransparent;
            }
            return CallWindowProc(_previous, hwnd, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_installed) return;
            SetWindowLongPtr(_hwnd, GwlWndProc, _previous);
            _installed = false;
        }
    }

    private delegate IntPtr NativeWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint
    {
        public NativePoint(int x, int y) { X = x; Y = y; }
        public int X;
        public int Y;
    }
}
