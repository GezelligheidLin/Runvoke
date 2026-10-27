using System.Runtime.InteropServices;

namespace Runvoke.WinUI.Core;

public sealed class TrayService : IDisposable
{
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint WmTray = 0x500;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint MenuShow = 1001;
    private const uint MenuStopAll = 1002;
    private const uint MenuExit = 1003;
    private bool _visible;
    private readonly IntPtr _windowHandle;
    private readonly Action _activate;
    private readonly Func<Task> _stopAll;
    private readonly Action _exit;
    private readonly WndProc _wndProc;
    private IntPtr _originalWndProc;

    public TrayService(IntPtr windowHandle, Action activate, Func<Task> stopAll, Action exit)
    {
        _windowHandle = windowHandle;
        _activate = activate;
        _stopAll = stopAll;
        _exit = exit;
        _wndProc = WindowProc;
        _originalWndProc = SetWindowLongPtr(_windowHandle, -4, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        var data = CreateData();
        Shell_NotifyIcon(visible ? NimAdd : NimDelete, ref data);
    }

    private NotifyIconData CreateData() => new()
    {
        CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        HWnd = _windowHandle,
        Uid = 1,
        UFlags = NifMessage | NifIcon | NifTip,
        UCallbackMessage = WmTray,
        HIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512),
        SzTip = "Runvoke",
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTray)
        {
            var eventCode = unchecked((uint)lParam.ToInt64());
            if (eventCode == WmLButtonDblClk) _activate();
            else if (eventCode == WmRButtonUp) ShowMenu();
        }
        return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (!GetCursorPos(out var point)) return;
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, (UIntPtr)MenuShow, "显示 Runvoke");
        AppendMenu(menu, 0, (UIntPtr)MenuStopAll, "停止全部任务");
        AppendMenu(menu, 0x00000800, UIntPtr.Zero, null);
        AppendMenu(menu, 0, (UIntPtr)MenuExit, "退出");
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenu(menu, 0x0100, point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
        DestroyMenu(menu);
        if (command == MenuShow) _activate();
        else if (command == MenuStopAll) _ = _stopAll();
        else if (command == MenuExit) _exit();
    }

    public void Dispose()
    {
        SetVisible(false);
        if (_originalWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, -4, _originalWndProc);
            _originalWndProc = IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint CbSize; public IntPtr HWnd; public uint Uid; public uint UFlags; public uint UCallbackMessage; public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string SzTip;
        public uint DwState; public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SzInfo;
        public uint UTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SzInfoTitle;
        public uint DwInfoFlags; public Guid GuidItem; public IntPtr HBalloonIcon;
    }
}
