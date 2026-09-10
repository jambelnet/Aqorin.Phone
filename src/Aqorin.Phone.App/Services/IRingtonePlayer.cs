using Aqorin.Phone.Core.Abstractions;

namespace Aqorin.Phone.App.Services;

public interface IRingtonePlayer : IDisposable
{
    bool IsPlaying { get; }

    void Start(string? outputDeviceId);

    void Stop();
}
