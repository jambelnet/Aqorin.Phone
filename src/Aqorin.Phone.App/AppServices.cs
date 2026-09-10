using Aqorin.Phone.App.Services;
using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.Audio;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Sip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App;

/// <summary>Composition root.</summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var diagnosticsLog = new DiagnosticsLog();

        services.AddSingleton(diagnosticsLog);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddFilter("Avalonia", LogLevel.Warning);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddProvider(new DiagnosticsLoggerProvider(diagnosticsLog));
        });

        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IContactStore, JsonContactStore>();
        services.AddSingleton<ICallHistoryStore, JsonCallHistoryStore>();
        services.AddSingleton<IUiPreferencesStore, JsonUiPreferencesStore>();
        services.AddSingleton<IRingtonePlayer, RingtonePlayer>();
        services.AddSingleton<IDiagnosticsSwitch, DiagnosticsSwitch>();

        // Audio: PortAudio. If the native library is missing the service reports IsAvailable=false.
        services.AddPortAudio(allowNullFallback: false);

        // SIP signalling, registration and RTP media.
        services.AddSipSorceryTelephony();

        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<DialerViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        SipServiceCollectionExtensions.UseSipSorceryLogging(provider.GetRequiredService<ILoggerFactory>());
        return provider;
    }

    public static async Task ShutdownAsync(ServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            await services.GetRequiredService<AccountViewModel>().SaveConfiguredSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving settings during shutdown failed.");
        }

        try
        {
            await services.GetRequiredService<MainWindowViewModel>().SavePreferencesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving UI preferences during shutdown failed.");
        }

        try
        {
            await services.GetRequiredService<ICallService>().DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Call service shutdown failed.");
        }

        try
        {
            await services.GetRequiredService<DialerViewModel>().SaveRecentCallsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving call history during shutdown failed.");
        }

        try
        {
            await services.GetRequiredService<ISipRegistrationService>().DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Registration service shutdown failed.");
        }

        try
        {
            services.GetRequiredService<IRingtonePlayer>().Stop();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ringtone shutdown failed.");
        }

        try
        {
            services.GetRequiredService<IAudioDeviceService>().Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audio shutdown failed.");
        }

        logger.LogInformation("Aqorin.Phone stopped.");
        await services.DisposeAsync().ConfigureAwait(false);
    }
}
