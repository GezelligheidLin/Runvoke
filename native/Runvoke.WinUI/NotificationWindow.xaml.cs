using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Foundation;

namespace Runvoke.WinUI;

public sealed partial class NotificationWindow : Window
{
    private readonly Dictionary<string, CancellationTokenSource> _timers = [];
    private readonly HashSet<string> _paused = [];
    private string _position = "bottom-right";
    private bool _stacking;
    private bool _reduceMotion;
    private bool _expanded;
    private bool _ready;
    private readonly TaskCompletionSource<bool> _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _hitTestHook;
    private IntPtr _ownerHandle;

    public ObservableCollection<NotificationItem> Items { get; } = [];
    public NotificationWindow ViewModel => this;
    public bool IsReady => _ready;
    public Task ReadyTask => _readySignal.Task;

    public NotificationWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            DisposeTimers();
            _hitTestHook?.Dispose();
            _hitTestHook = null;
        };
        Activated += (_, _) => NativeWindowStyles.SetNoActivate(WindowNative.GetWindowHandle(this));
    }

    public void SetOwnerHandle(IntPtr ownerHandle) => _ownerHandle = ownerHandle;

    public void Configure(string position, bool reduceMotion, bool stacking = false)
    {
        _position = position;
        _reduceMotion = reduceMotion;
        _stacking = stacking;
        if (_ready) Reposition();
    }

    public void SetTheme(ElementTheme theme) => Root.RequestedTheme = theme;

    public void Prepare(string position, bool reduceMotion, bool stacking = false)
    {
        Configure(position, reduceMotion, stacking);
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeWindowStyles.MakeNotificationWindow(hwnd);
        _hitTestHook ??= NativeWindowStyles.InstallTransparentHitTest(hwnd, IsPointInsideCard);
        ShowWithoutFocus();
        HideWindow();
        _ready = true;
        _readySignal.TrySetResult(true);
    }

    public void ShowNotification(string title, string message, bool reduceMotion, string? position = null)
    {
        if (!_ready) return;
        _reduceMotion = reduceMotion;
        var item = new NotificationItem(Guid.NewGuid().ToString("N"), title, message);
        Items.Insert(0, item);
        while (Items.Count > 3) Remove(Items[^1].Id);
        StartTimer(item, TimeSpan.FromSeconds(6));
        var previousPosition = _position;
        if (!string.IsNullOrWhiteSpace(position)) _position = position;
        Reposition();
        Root.UpdateLayout();
        ShowWithoutFocus();
        PlayFadeIn();
        if (!string.IsNullOrWhiteSpace(position)) _position = previousPosition;
    }

    private void StartTimer(NotificationItem item, TimeSpan duration)
    {
        if (_timers.Remove(item.Id, out var old)) old.Dispose();
        var timer = new CancellationTokenSource();
        _timers[item.Id] = timer;
        _ = ExpireAsync(item.Id, duration, timer.Token);
    }

    private async Task ExpireAsync(string id, TimeSpan duration, CancellationToken cancellationToken)
    {
        var remaining = duration;
        var started = DateTime.UtcNow;
        try
        {
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                if (!_paused.Contains(id))
                {
                    remaining -= DateTime.UtcNow - started;
                    started = DateTime.UtcNow;
                }
                else started = DateTime.UtcNow;
            }
            DispatcherQueue.TryEnqueue(() => Remove(id));
        }
        catch (OperationCanceledException) { }
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is NotificationItem item) _paused.Add(item.Id);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is NotificationItem item) _paused.Remove(item.Id);
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (!_stacking || args.OriginalSource is Button) return;
        if (!_expanded && (sender as FrameworkElement)?.DataContext is NotificationItem item && Items.IndexOf(item) != 0) return;
        _expanded = !_expanded;
        Reposition();
    }

    private void Cards_LayoutUpdated(object sender, object args)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (Cards.ContainerFromIndex(index) is not FrameworkElement container) continue;
            container.Width = 360;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(container, 0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(container, _stacking && !_expanded ? index * 30 : index * 112);
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(container, Items.Count - index);
        }
    }

    private bool IsPointInsideCard(int x, int y)
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var point = new Point(x / scale, y / scale);
        for (var index = 0; index < Items.Count; index++)
        {
            if (Cards.ContainerFromIndex(index) is not FrameworkElement container) continue;
            var origin = container.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            var bounds = new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
            if (bounds.Contains(point)) return true;
        }
        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is string id) Remove(id);
    }

    private void OverflowText_Loaded(object sender, RoutedEventArgs args) => UpdateOverflowTooltip(sender as FrameworkElement);

    private void OverflowText_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateOverflowTooltip(sender as FrameworkElement);

    private static void UpdateOverflowTooltip(FrameworkElement? element)
    {
        if (element is null) return;
        var text = element is TextBlock block ? block.Text : string.Empty;
        var overflow = !string.IsNullOrWhiteSpace(text) && element.ActualWidth > 0 && element.DesiredSize.Width > element.ActualWidth + 0.5;
        ToolTipService.SetToolTip(element, overflow ? text : null);
    }

    private void Remove(string id)
    {
        var item = Items.FirstOrDefault(value => value.Id == id);
        if (item is not null) Items.Remove(item);
        if (_timers.Remove(id, out var timer)) timer.Dispose();
        _paused.Remove(id);
        if (Items.Count == 0)
        {
            if (_reduceMotion) HideWindow();
            else PlayFadeOutAndHide();
        }
        else Reposition();
    }

    private void PlayFadeIn()
    {
        if (_reduceMotion)
        {
            Root.Opacity = 1;
            return;
        }
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        Root.Opacity = 0;
        storyboard.Begin();
    }

    private void PlayFadeOutAndHide()
    {
        if (_reduceMotion)
        {
            HideWindow();
            return;
        }
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = Root.Opacity,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (Items.Count == 0)
            {
                Root.Opacity = 1;
                HideWindow();
            }
        };
        storyboard.Begin();
    }

    private void ShowWithoutFocus()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeWindowStyles.MakeNotificationWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Show();
        NativeWindowStyles.Position(hwnd, appWindow.Size.Width, appWindow.Size.Height, _position, _ownerHandle);
        NativeWindowStyles.SetNoActivate(hwnd);
    }

    private void Reposition()
    {
        if (!_ready) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        var height = _stacking && !_expanded
            ? Math.Max(180, 32 + 118 + Math.Max(0, Items.Count - 1) * 30)
            : Math.Max(120, 32 + Items.Count * 112);
        appWindow.Resize(new SizeInt32(390, height));
        if (Items.Count > 0) NativeWindowStyles.Position(hwnd, appWindow.Size.Width, appWindow.Size.Height, _position, _ownerHandle);
    }

    private void HideWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd)).Hide();
    }

    private void DisposeTimers()
    {
        foreach (var timer in _timers.Values) timer.Dispose();
        _timers.Clear();
    }

    public sealed record NotificationItem(string Id, string Title, string Message);
}
