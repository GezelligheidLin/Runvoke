using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Threading;

namespace Runvoke.WinUI;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    public Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(true, "Local\\Runvoke.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            Environment.Exit(0);
            return;
        }
        MainWindow ??= new MainWindow();
        MainWindow.Activate();
    }

    private static void ActivateExistingWindow()
    {
        var handle = IntPtr.Zero;
        for (var attempt = 0; attempt < 40 && handle == IntPtr.Zero; attempt++)
        {
            handle = FindWindow(null, "Runvoke");
            if (handle == IntPtr.Zero) Thread.Sleep(50);
        }
        if (handle == IntPtr.Zero) return;
        if (IsIconic(handle)) ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
}
