using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using PortAudioSharp;

namespace Aqorin.Phone.Audio.PortAudio;

/// <summary>
/// <see cref="IAudioDeviceService"/> backed by PortAudio (via the PortAudioSharp2 binding, Apache-2.0).
/// The native library ships inside the NuGet runtime packages for win-x64, linux-x64, linux-aarch64, osx-x64 and osx-arm64
/// and is placed in <c>runtimes/&lt;rid&gt;/native/</c> (or next to the executable after a RID-specific publish).
/// Host APIs: WASAPI/WDM-KS/MME on Windows, CoreAudio on macOS, ALSA (and JACK if present) on Linux.
/// </summary>
public sealed class PortAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<PortAudioDeviceService> _logger;
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;

    public PortAudioDeviceService(ILogger<PortAudioDeviceService> logger)
    {
        _logger = logger;
        Backend = Probe();
    }

    public AudioBackendInfo Backend { get; }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => EnumerateDevices(input: true);

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => EnumerateDevices(input: false);

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAvailable();
        var device = ResolveDevice(request.DeviceId, input: true);
        return new PortAudioCaptureStream(request, device.Index, device.Info, _logger);
    }

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAvailable();
        var device = ResolveDevice(request.DeviceId, input: false);
        return new PortAudioPlaybackStream(request, device.Index, device.Info, _logger);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_initialized)
            {
                try
                {
                    PortAudioSharp.PortAudio.Terminate();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PortAudio terminate failed.");
                }
            }
        }
    }

    private AudioBackendInfo Probe()
    {
        try
        {
            lock (_gate)
            {
                PortAudioSharp.PortAudio.Initialize();
                _initialized = true;
            }

            // versionText looks like "PortAudio V19.7.0-devel, revision <sha>"; keep the short form for the UI.
            var versionText = PortAudioSharp.PortAudio.VersionInfo.versionText ?? string.Empty;
            var comma = versionText.IndexOf(',');
            var version = (comma > 0 ? versionText[..comma] : versionText).Replace("PortAudio ", string.Empty, StringComparison.Ordinal).Trim();
            var count = PortAudioSharp.PortAudio.DeviceCount;
            _logger.LogInformation("PortAudio initialised: {Version}, {DeviceCount} device(s).", version, count);
            return new AudioBackendInfo("PortAudio", version, IsAvailable: true, null);
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "PortAudio native library could not be loaded.");
            return new AudioBackendInfo("PortAudio", null, false,
                "The native PortAudio library was not found. Publish/run for your platform's runtime identifier and, on Linux, install libasound2 (and libjack0). Details: " + ex.Message);
        }
        catch (Exception ex) when (ex is PortAudioException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            _logger.LogError(ex, "PortAudio initialisation failed.");
            return new AudioBackendInfo("PortAudio", null, false, "PortAudio could not be initialised: " + ex.Message);
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Backend.IsAvailable)
        {
            throw new AudioDeviceException(Backend.UnavailableReason ?? "Audio backend unavailable.");
        }
    }

    private IReadOnlyList<AudioDeviceInfo> EnumerateDevices(bool input)
    {
        if (!Backend.IsAvailable || _disposed)
        {
            return Array.Empty<AudioDeviceInfo>();
        }

        var list = new List<AudioDeviceInfo>();
        try
        {
            var defaultIndex = input ? PortAudioSharp.PortAudio.DefaultInputDevice : PortAudioSharp.PortAudio.DefaultOutputDevice;
            var count = PortAudioSharp.PortAudio.DeviceCount;
            for (var i = 0; i < count; i++)
            {
                var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                var channels = input ? info.maxInputChannels : info.maxOutputChannels;
                if (channels <= 0)
                {
                    continue;
                }

                list.Add(new AudioDeviceInfo(DeviceId(i, info.name), info.name, i == defaultIndex, info.maxInputChannels, info.maxOutputChannels, info.defaultSampleRate));
            }
        }
        catch (PortAudioException ex)
        {
            _logger.LogWarning(ex, "Enumerating audio devices failed.");
        }

        return list;
    }

    internal static string DeviceId(int index, string name) => $"{index}:{name}";

    private (int Index, DeviceInfo Info) ResolveDevice(string? deviceId, bool input)
    {
        var count = PortAudioSharp.PortAudio.DeviceCount;
        if (!string.IsNullOrEmpty(deviceId))
        {
            // Prefer a name match (indices shift when devices are plugged in), then fall back to the index prefix.
            var colon = deviceId.IndexOf(':');
            var name = colon >= 0 ? deviceId[(colon + 1)..] : deviceId;
            for (var i = 0; i < count; i++)
            {
                var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                if (string.Equals(info.name, name, StringComparison.Ordinal) && (input ? info.maxInputChannels : info.maxOutputChannels) > 0)
                {
                    return (i, info);
                }
            }

            if (colon > 0 && int.TryParse(deviceId[..colon], out var index) && index >= 0 && index < count)
            {
                var info = PortAudioSharp.PortAudio.GetDeviceInfo(index);
                if ((input ? info.maxInputChannels : info.maxOutputChannels) > 0)
                {
                    return (index, info);
                }
            }

            _logger.LogWarning("Audio device {DeviceId} not found; using the default {Direction} device.", deviceId, input ? "input" : "output");
        }

        var defaultIndex = input ? PortAudioSharp.PortAudio.DefaultInputDevice : PortAudioSharp.PortAudio.DefaultOutputDevice;
        if (defaultIndex == PortAudioSharp.PortAudio.NoDevice || defaultIndex < 0 || defaultIndex >= count)
        {
            throw new AudioDeviceException(input
                ? "No microphone (input device) is available."
                : "No speaker (output device) is available.");
        }

        return (defaultIndex, PortAudioSharp.PortAudio.GetDeviceInfo(defaultIndex));
    }
}
