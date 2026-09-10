using Avalonia.Data.Converters;
using Avalonia.Media;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.App.Views;

public static class Converters
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly IBrush White = Brushes.White;

    /// <summary>true → green, false → grey.</summary>
    public static readonly IValueConverter GoodBadBrush = new FuncValueConverter<bool, IBrush>(good => good ? Good : Neutral);

    public static readonly IValueConverter MissedCallBrush = new FuncValueConverter<bool, IBrush>(missed => missed ? Bad : Neutral);

    public static readonly IValueConverter SelectedSegmentBrush = new FuncValueConverter<bool, IBrush>(selected => selected ? Info : Transparent);

    public static readonly IValueConverter SelectedSegmentForegroundBrush = new FuncValueConverter<bool, IBrush>(selected => selected ? White : Neutral);

    public static readonly IValueConverter RegistrationStateBrush = new FuncValueConverter<RegistrationState, IBrush>(state => state switch
    {
        RegistrationState.Registered => Good,
        RegistrationState.Registering or RegistrationState.Unregistering => Warn,
        RegistrationState.Failed => Bad,
        _ => Neutral,
    });

    public static readonly IValueConverter CallStateBrush = new FuncValueConverter<CallState, IBrush>(state => state switch
    {
        CallState.Active => Good,
        CallState.Incoming => Info,
        CallState.Dialing or CallState.Ringing or CallState.Connecting or CallState.Ending => Warn,
        CallState.Failed => Bad,
        _ => Neutral,
    });
}
