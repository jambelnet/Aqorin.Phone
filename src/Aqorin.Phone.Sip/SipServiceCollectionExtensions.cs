using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Sip.Internal;
using Aqorin.Phone.Sip.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Sip;

public static class SipServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SIPSorcery-backed <see cref="ISipRegistrationService"/>, <see cref="ICallService"/> and
    /// <see cref="IAudioMediaSessionFactory"/>. Requires an <see cref="IAudioDeviceService"/> and logging to be registered.
    /// </summary>
    public static IServiceCollection AddSipSorceryTelephony(this IServiceCollection services)
    {
        services.TryAddSingleton<SipDiagnosticsOptions>();
        services.TryAddSingleton<SipSessionContext>();
        services.TryAddSingleton<ISipTransportFactory, SipSorceryTransportFactory>();
        services.TryAddSingleton<ISipRegistrationClientFactory, SipSorceryRegistrationClientFactory>();
        services.TryAddSingleton<ISipUserAgentFactory, SipSorceryUserAgentFactory>();
        services.TryAddSingleton<IAudioMediaSessionFactory, SipSorceryAudioMediaSessionFactory>();
        services.TryAddSingleton<ISipRegistrationService>(sp => new SipRegistrationService(
            sp.GetRequiredService<ISipTransportFactory>(),
            sp.GetRequiredService<ISipRegistrationClientFactory>(),
            sp.GetRequiredService<SipSessionContext>(),
            sp.GetRequiredService<ILogger<SipRegistrationService>>()));
        services.TryAddSingleton<ICallService>(sp => new SipCallService(
            sp.GetRequiredService<ISipUserAgentFactory>(),
            sp.GetRequiredService<IAudioMediaSessionFactory>(),
            sp.GetRequiredService<SipSessionContext>(),
            sp.GetRequiredService<ILogger<SipCallService>>()));
        return services;
    }

    /// <summary>Routes SIPSorcery's internal logging through the application's logger factory with credential redaction.</summary>
    public static void UseSipSorceryLogging(ILoggerFactory loggerFactory)
    {
        SIPSorcery.LogFactory.Set(new RedactingLoggerFactory(loggerFactory));
    }
}
