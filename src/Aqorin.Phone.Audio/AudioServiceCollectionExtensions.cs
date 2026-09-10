using Aqorin.Phone.Audio.PortAudio;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Audio;

public static class AudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PortAudio backend. If the native library cannot be loaded the service still resolves and reports
    /// <see cref="AudioBackendInfo.IsAvailable"/> = false so the UI can show the reason instead of crashing.
    /// Set <paramref name="allowNullFallback"/> to substitute a silent backend (never done implicitly for the desktop app).
    /// </summary>
    public static IServiceCollection AddPortAudio(this IServiceCollection services, bool allowNullFallback = false)
    {
        services.TryAddSingleton<IAudioDeviceService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PortAudioDeviceService>>();
            var service = new PortAudioDeviceService(logger);
            if (!service.Backend.IsAvailable && allowNullFallback)
            {
                logger.LogWarning("Falling back to the silent audio backend: {Reason}", service.Backend.UnavailableReason);
                service.Dispose();
                return new NullAudioDeviceService(service.Backend.UnavailableReason);
            }

            return service;
        });
        return services;
    }
}
