using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Aqorin.Phone.Mobile.Platforms.Android;

[Application(
    Label = "Aqorin Phone",
    Theme = "@style/Theme.AqorinPhone",
    Icon = "@drawable/aqorin_phone")]
public sealed class MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<global::Aqorin.Phone.App.App>(javaReference, transfer)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace();
}
