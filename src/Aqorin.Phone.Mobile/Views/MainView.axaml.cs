using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Aqorin.Phone.Mobile.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    internal void RefreshAfterForeground()
    {
        Dispatcher.UIThread.Post(RefreshLayout, DispatcherPriority.Background);
        _ = RefreshAfterWindowSettlesAsync();
    }

    private async Task RefreshAfterWindowSettlesAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);
        Dispatcher.UIThread.Post(RefreshLayout, DispatcherPriority.Background);
    }

    private void RefreshLayout()
    {
        MainTabs.InvalidateMeasure();
        MainTabs.InvalidateArrange();
        MainTabs.InvalidateVisual();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.InvalidateMeasure();
            topLevel.InvalidateArrange();
            topLevel.InvalidateVisual();
        }
    }
}
