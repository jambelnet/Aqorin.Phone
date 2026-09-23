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
    private static WeakReference<MainActivity>? _current;
    private long _backgroundedAt;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _current = new WeakReference<MainActivity>(this);

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            RequestCallPermissions();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        _current = new WeakReference<MainActivity>(this);
        RefreshForegroundLayout();

        var backgroundedAt = Interlocked.Exchange(ref _backgroundedAt, 0);
        if (backgroundedAt > 0 && System.Environment.TickCount64 - backgroundedAt >= 5_000)
        {
            _ = AndroidCallRuntime.RefreshRegistrationAsync();
        }
    }

    protected override void OnPause()
    {
        Interlocked.Exchange(ref _backgroundedAt, System.Environment.TickCount64);
        base.OnPause();
    }

    internal static void RequestBackgroundCallingAccess()
    {
        var current = _current;
        if (current is null || !current.TryGetTarget(out var activity))
        {
            return;
        }

        if (activity.IsFinishing || activity.IsDestroyed)
        {
            return;
        }

        activity.RunOnUiThread(() => AndroidBatteryOptimization.RequestOnce(activity));
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
