using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Runvoke.WinUI.Core;

public sealed class NotificationService : IDisposable
{
    private bool _registered;
    private Runvoke.WinUI.NotificationWindow? _window;

    public NotificationService()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch { }
    }

    public void Initialize(Runvoke.WinUI.NotificationWindow window, string position, bool reduceMotion, bool stacking = false, IntPtr ownerHandle = default)
    {
        _window = window;
        _window.SetOwnerHandle(ownerHandle);
        _window.Prepare(position, reduceMotion, stacking);
    }

    public void Configure(string position, bool reduceMotion, bool stacking = false) => _window?.Configure(position, reduceMotion, stacking);

    public void Show(string title, string message, bool reduceMotion = false) =>
        ShowAt(title, message, reduceMotion, null);

    public void ShowAt(string title, string message, bool reduceMotion, string? position)
    {
        if (_window is not null)
        {
            if (_window.IsReady)
                _window.ShowNotification(title, message, reduceMotion, position);
            else
                _ = ShowWhenReadyAsync(_window, title, message, reduceMotion, position);
            return;
        }
        if (!_registered) return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch { }
    }

    private static async Task ShowWhenReadyAsync(Runvoke.WinUI.NotificationWindow window, string title, string message, bool reduceMotion, string? position)
    {
        try
        {
            await window.ReadyTask.ConfigureAwait(true);
            window.DispatcherQueue.TryEnqueue(() => window.ShowNotification(title, message, reduceMotion, position));
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _window?.Close(); } catch { }
        _window = null;
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }
}
