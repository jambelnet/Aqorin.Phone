using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace Aqorin.Phone.Mobile.Platforms.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<global::Aqorin.Phone.App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace();
}
