using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Net.Wifi;
using Android.OS;
using System.Runtime.Versioning;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Aqorin.Phone.Mobile.Platforms.Android;

[Service(
    Name = "com.aqorin.phone.SipForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypePhoneCall | ForegroundService.TypeMicrophone)]
public sealed class SipForegroundService : Service
{
    internal const string ActionAnswer = "com.aqorin.phone.action.ANSWER";
    internal const string ActionReject = "com.aqorin.phone.action.REJECT";
    internal const string ActionHangup = "com.aqorin.phone.action.HANGUP";
    private const string ActionUpdate = "com.aqorin.phone.action.UPDATE";
    private const string AvailabilityChannel = "aqorin_availability";
    private const string CallsChannel = "aqorin_calls";
    private const int NotificationId = 620;
    private static readonly object StateGate = new();
    private static RegistrationStatus _registration = RegistrationStatus.Disconnected;
    private static CallInfo _call = CallInfo.Idle;
    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private DeviceIdleReceiver? _deviceIdleReceiver;
    private global::Android.Net.ConnectivityManager? _connectivityManager;
    private DefaultNetworkCallback? _networkCallback;
    private CancellationTokenSource? _networkRefresh;
    private CancellationTokenSource? _registrationWatchdog;
    private int _defaultNetworkSeen;
    private int _recoveryStarted;
    private long _lastIdleRefresh;

    public static void StartOrUpdate(RegistrationStatus registration, CallInfo call)
    {
        lock (StateGate)
        {
            _registration = registration;
            _call = call;
        }

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(SipForegroundService)).SetAction(ActionUpdate);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(SipForegroundService)));
    }

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannels();
        AcquireAvailabilityLocks();
        RegisterDeviceIdleReceiver();
        RegisterDefaultNetworkCallback();
        StartRegistrationWatchdog();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (action is ActionAnswer or ActionReject or ActionHangup)
        {
            _ = AndroidCallRuntime.ExecuteAsync(action);
        }

        UpdateForegroundNotification();
        if (intent is null || !AndroidCallRuntime.IsAttached)
        {
            RestoreRuntime();
        }

        return StartCommandResult.Sticky;
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        StopRegistrationWatchdog();
        UnregisterDefaultNetworkCallback();
        UnregisterDeviceIdleReceiver();
        ReleaseAvailabilityLocks();
        base.OnDestroy();
    }

    private void UpdateForegroundNotification()
    {
        RegistrationStatus registration;
        CallInfo call;
        lock (StateGate)
        {
            registration = _registration;
            call = _call;
        }

        var notification = BuildNotification(registration, call);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var serviceType = ForegroundService.TypePhoneCall;
            if (OperatingSystem.IsAndroidVersionAtLeast(30)
                && call.State is (CallState.Connecting or CallState.Active))
            {
                serviceType |= ForegroundService.TypeMicrophone;
            }

            StartForeground(NotificationId, notification, serviceType);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private Notification BuildNotification(RegistrationStatus registration, CallInfo call)
    {
        var isIncoming = call.State == CallState.Incoming;
        var hasCall = call.IsInProgress;
        var channel = hasCall ? CallsChannel : AvailabilityChannel;
        var title = isIncoming
            ? "Incoming call"
            : hasCall
                ? "Call in progress"
                : registration.IsRegistered ? "Aqorin Phone is available" : "Restoring Aqorin Phone";
        var text = isIncoming
            ? $"Call from {call.RemoteParty}"
            : hasCall ? call.Message : registration.Message;

        var openIntent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentIntent = PendingIntent.GetActivity(
            this,
            1,
            openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = CreateNotificationBuilder(channel)
            .SetSmallIcon(global::Android.Resource.Drawable.SymActionCall)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetContentIntent(contentIntent)
            .SetCategory(Notification.CategoryCall)
            .SetVisibility(NotificationVisibility.Public)
            .SetOngoing(!isIncoming)
            .SetOnlyAlertOnce(!isIncoming);

        if (isIncoming)
        {
            builder.AddAction(BuildAction("Answer", ActionAnswer, 10));
            builder.AddAction(BuildAction("Reject", ActionReject, 11));
        }
        else if (hasCall)
        {
            builder.AddAction(BuildAction("Hang up", ActionHangup, 12));
        }
        else if (!AndroidBatteryOptimization.IsExempt(this))
        {
            builder.AddAction(BuildActivityAction(
                "Allow background",
                AndroidBatteryOptimization.CreateRequestPendingIntent(this, 13)));
        }

        return builder.Build();
    }

    private void RestoreRuntime()
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var services = global::Aqorin.Phone.App.AppServices.Build();
                await services.GetRequiredService<AndroidRegistrationRecovery>().RestoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Aqorin.Phone", $"Could not restore the SIP runtime: {ex}");
                Stop();
            }
        });
    }

    private PendingIntent ServiceAction(string action, int requestCode) =>
        PendingIntent.GetService(
            this,
            requestCode,
            new Intent(this, typeof(SipForegroundService)).SetAction(action),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

    private Notification.Action BuildAction(string title, string action, int requestCode)
    {
        using var icon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.SymActionCall);
        using var builder = new Notification.Action.Builder(icon, title, ServiceAction(action, requestCode));
        return builder.Build();
    }

    private Notification.Action BuildActivityAction(string title, PendingIntent action)
    {
        using var icon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.SymActionCall);
        using var builder = new Notification.Action.Builder(icon, title, action);
        return builder.Build();
    }

    private Notification.Builder CreateNotificationBuilder(string channel) =>
        OperatingSystem.IsAndroidVersionAtLeast(26)
            ? CreateChannelNotificationBuilder(channel)
            : new Notification.Builder(this);

    [SupportedOSPlatform("android26.0")]
    private Notification.Builder CreateChannelNotificationBuilder(string channel) => new(this, channel);

    private void CreateNotificationChannels()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)
            || GetSystemService(NotificationService) is not NotificationManager manager)
        {
            return;
        }

        manager.CreateNotificationChannel(new NotificationChannel(
            AvailabilityChannel,
            "SIP availability",
            NotificationImportance.Low)
        {
            Description = "Keeps Aqorin registered for incoming calls."
        });

        manager.CreateNotificationChannel(new NotificationChannel(
            CallsChannel,
            "Calls",
            NotificationImportance.High)
        {
            Description = "Incoming and active Aqorin calls."
        });
    }

    private void AcquireAvailabilityLocks()
    {
        if (GetSystemService(PowerService) is PowerManager powerManager)
        {
            _wakeLock = powerManager.NewWakeLock(WakeLockFlags.Partial, "Aqorin.Phone:SipAvailability");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }

        if (GetSystemService(WifiService) is WifiManager wifiManager)
        {
#pragma warning disable CS0618
            _wifiLock = wifiManager.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "Aqorin.Phone:SipWifi");
#pragma warning restore CS0618
            _wifiLock?.SetReferenceCounted(false);
            _wifiLock?.Acquire();
        }
    }

    private void RegisterDeviceIdleReceiver()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        _deviceIdleReceiver = new DeviceIdleReceiver(this);
#pragma warning disable CA1422 // This overload is valid for protected system broadcasts.
        RegisterReceiver(_deviceIdleReceiver, new IntentFilter(PowerManager.ActionDeviceIdleModeChanged));
#pragma warning restore CA1422
    }

    private void UnregisterDeviceIdleReceiver()
    {
        if (_deviceIdleReceiver is null)
        {
            return;
        }

        try
        {
            UnregisterReceiver(_deviceIdleReceiver);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }

        _deviceIdleReceiver.Dispose();
        _deviceIdleReceiver = null;
    }

    private void OnDeviceIdleModeChanged()
    {
        if (GetSystemService(PowerService) is PowerManager { IsDeviceIdleMode: true })
        {
            return;
        }

        var now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastIdleRefresh) < 30_000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastIdleRefresh, now);
        if (AndroidCallRuntime.IsAttached)
        {
            _ = AndroidCallRuntime.RefreshRegistrationAsync();
        }
        else
        {
            RestoreRuntime();
        }
    }

    private void RegisterDefaultNetworkCallback()
    {
        if (GetSystemService(ConnectivityService) is not global::Android.Net.ConnectivityManager manager)
        {
            return;
        }

        _connectivityManager = manager;
        _networkCallback = new DefaultNetworkCallback(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            manager.RegisterDefaultNetworkCallback(_networkCallback);
            return;
        }

        using var request = new global::Android.Net.NetworkRequest.Builder().Build();
        manager.RegisterNetworkCallback(request!, _networkCallback);
    }

    private void UnregisterDefaultNetworkCallback()
    {
        _networkRefresh?.Cancel();
        _networkRefresh?.Dispose();
        _networkRefresh = null;

        if (_connectivityManager is not null && _networkCallback is not null)
        {
            try
            {
                _connectivityManager.UnregisterNetworkCallback(_networkCallback);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }
        }

        _networkCallback?.Dispose();
        _networkCallback = null;
        _connectivityManager = null;
    }

    private void OnDefaultNetworkAvailable()
    {
        if (Interlocked.Exchange(ref _defaultNetworkSeen, 1) == 0)
        {
            return;
        }

        var refresh = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _networkRefresh, refresh);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), refresh.Token).ConfigureAwait(false);
                if (AndroidCallRuntime.IsAttached)
                {
                    await AndroidCallRuntime.RefreshRegistrationAsync().ConfigureAwait(false);
                }
                else
                {
                    RestoreRuntime();
                }
            }
            catch (System.OperationCanceledException)
            {
            }
        });
    }

    private void StartRegistrationWatchdog()
    {
        var watchdog = new CancellationTokenSource();
        _registrationWatchdog = watchdog;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), watchdog.Token).ConfigureAwait(false);
                    if (AndroidCallRuntime.IsAttached)
                    {
                        await AndroidCallRuntime.MaintainRegistrationAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        RestoreRuntime();
                    }
                }
            }
            catch (System.OperationCanceledException) when (watchdog.IsCancellationRequested)
            {
            }
            finally
            {
                watchdog.Dispose();
            }
        });
    }

    private void StopRegistrationWatchdog()
    {
        Interlocked.Exchange(ref _registrationWatchdog, null)?.Cancel();
    }

    private void ReleaseAvailabilityLocks()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }

        if (_wifiLock?.IsHeld == true)
        {
            _wifiLock.Release();
        }

        _wakeLock?.Dispose();
        _wifiLock?.Dispose();
        _wakeLock = null;
        _wifiLock = null;
    }

    private sealed class DeviceIdleReceiver(SipForegroundService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == PowerManager.ActionDeviceIdleModeChanged)
            {
                owner.OnDeviceIdleModeChanged();
            }
        }
    }

    private sealed class DefaultNetworkCallback(SipForegroundService owner)
        : global::Android.Net.ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(global::Android.Net.Network network)
        {
            base.OnAvailable(network);
            owner.OnDefaultNetworkAvailable();
        }
    }
}
