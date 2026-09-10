using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Abstractions;

/// <summary>What was read back from persistent storage. <see cref="Password"/> is null when none was stored.</summary>
public sealed record StoredSettings(SipAccountSettings Settings, string? Password);

/// <summary>
/// Persists account settings. The password is optional and, when stored, must be encrypted at rest —
/// implementations must never write it as plaintext.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Human-readable description of how the password is protected (shown in the UI).</summary>
    string PasswordStorageDescription { get; }

    Task<StoredSettings?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the settings; passing a null <paramref name="password"/> removes any stored password.</summary>
    Task SaveAsync(SipAccountSettings settings, string? password, CancellationToken cancellationToken = default);
}
