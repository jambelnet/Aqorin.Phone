using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aqorin.Phone.App.ViewModels;

namespace Aqorin.Phone.App.Views;

public partial class DialerView : UserControl
{
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _longPressTimer;
    private Button? _pressedButton;
    private KeypadButton? _pressedKey;
    private IPointer? _pressedPointer;
    private int _alternateIndex;
    private bool _longPressTriggered;

    public DialerView()
    {
        InitializeComponent();

        _longPressTimer = new DispatcherTimer { Interval = LongPressDelay };
        _longPressTimer.Tick += LongPressTimer_OnTick;
        AddHandler(PointerPressedEvent, KeypadButton_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, KeypadButton_OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void KeypadButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = (e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true);
        if (button is null || !button.Classes.Contains("dialPadButton") || button.DataContext is not KeypadButton key)
        {
            return;
        }

        ResetPressState();
        _pressedButton = button;
        _pressedKey = key;
        _pressedPointer = e.Pointer;
        _alternateIndex = 0;
        e.Pointer.Capture(button);
        _longPressTimer.Start();
    }

    private void KeypadButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _pressedPointer)
        {
            return;
        }

        _longPressTimer.Stop();
        Dispatcher.UIThread.Post(ResetPressState, DispatcherPriority.Background);
    }

    private void KeypadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not KeypadButton key)
        {
            return;
        }

        if (_longPressTriggered && button == _pressedButton)
        {
            return;
        }

        if (DataContext is DialerViewModel viewModel)
        {
            viewModel.AppendKeypadValue(key.Value);
        }
    }

    private void LongPressTimer_OnTick(object? sender, EventArgs e)
    {
        if (_pressedKey is not { HasAlternates: true } key || DataContext is not DialerViewModel viewModel)
        {
            ResetPressState();
            return;
        }

        var alternate = key.Alternates[_alternateIndex].ToString();
        if (_longPressTriggered)
        {
            viewModel.ReplaceLastKeypadValue(alternate);
        }
        else
        {
            viewModel.AppendKeypadValue(alternate);
            _longPressTriggered = true;
        }

        _alternateIndex = (_alternateIndex + 1) % key.Alternates.Length;
        if (key.Alternates.Length == 1)
        {
            _longPressTimer.Stop();
        }
    }

    private void ResetPressState()
    {
        _longPressTimer.Stop();
        _pressedButton = null;
        _pressedKey = null;
        _pressedPointer = null;
        _alternateIndex = 0;
        _longPressTriggered = false;
    }
}
