using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using System.Runtime.Versioning;

namespace Aqorin.Phone.Mobile.Platforms.Android;

[Activity(
    Label = "Aqorin Phone",
    Theme = "@style/Theme.AqorinPhone",
    Icon = "@drawable/aqorin_phone",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode
                           | ConfigChanges.KeyboardHidden
                           | ConfigChanges.Navigation)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int CallPermissionRequest = 1001;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            RequestCallPermissions();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshForegroundLayout();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            RefreshForegroundLayout();
        }
    }

    private void RefreshForegroundLayout()
    {
        Window?.DecorView?.RequestApplyInsets();
        Window?.DecorView?.RequestLayout();
        Window?.DecorView?.Invalidate();

        if (Avalonia.Application.Current is global::Aqorin.Phone.App.App app)
        {
            app.RefreshMainViewLayout();
        }
    }

    [SupportedOSPlatform("android23.0")]
    private void RequestCallPermissions()
    {
        var permissions = new List<string>();
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            permissions.Add(global::Android.Manifest.Permission.RecordAudio);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            permissions.Add(global::Android.Manifest.Permission.PostNotifications);
        }

        if (permissions.Count > 0)
        {
            RequestPermissions([.. permissions], CallPermissionRequest);
        }
    }
}
