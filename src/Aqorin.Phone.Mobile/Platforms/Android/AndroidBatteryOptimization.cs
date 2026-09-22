using Android.App;
using Android.Content;
using Android.OS;

namespace Aqorin.Phone.Mobile.Platforms.Android;

internal static class AndroidBatteryOptimization
{
    private const string PreferencesName = "aqorin_background_policy";
    private const string PromptedKey = "battery_optimization_prompted";

    public static bool IsExempt(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return true;
        }

        return context.GetSystemService(Context.PowerService) is PowerManager manager
            && manager.IsIgnoringBatteryOptimizations(context.PackageName!);
    }

    public static PendingIntent CreateRequestPendingIntent(Context context, int requestCode)
    {
        var intent = CreateRequestIntent(context);
        return PendingIntent.GetActivity(
            context,
            requestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public static void RequestOnce(Activity activity)
    {
        if (IsExempt(activity))
        {
            return;
        }

        var preferences = activity.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        if (preferences?.GetBoolean(PromptedKey, false) == true)
        {
            return;
        }

        preferences?.Edit()?.PutBoolean(PromptedKey, true)?.Apply();
        try
        {
            activity.StartActivity(CreateRequestIntent(activity));
        }
        catch (ActivityNotFoundException)
        {
            activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
        }
    }

    private static Intent CreateRequestIntent(Context context) =>
        new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
            .SetData(global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
}
