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
    private const int RecordAudioPermissionRequest = 1001;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            RequestRecordAudioPermission();
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
    private void RequestRecordAudioPermission()
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            RequestPermissions([global::Android.Manifest.Permission.RecordAudio], RecordAudioPermissionRequest);
        }
    }
}
