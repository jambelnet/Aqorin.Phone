using System.Text.Json;
using System.Text.Json.Serialization;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

/// <summary>
/// Stores settings as JSON under the per-user application data folder
/// (%APPDATA%\Aqorin.Phone on Windows, ~/.config/Aqorin.Phone on Linux, ~/Library/Application Support/Aqorin.Phone on macOS).
/// The password is stored only in encrypted form (see <see cref="IPasswordProtector"/>) and only when the user chose
/// to remember it; it is never written as plaintext and never logged.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly IPasswordProtector _protector;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Aqorin.Phone"), null)
    {
    }

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger, string directory, IPasswordProtector? protector)
    {
        _logger = logger;
        Directory = directory;
        FilePath = Path.Combine(directory, "settings.json");
        _protector = protector ?? (OperatingSystem.IsWindows() ? new DpapiPasswordProtector() : new UserKeyFilePasswordProtector(directory));
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string PasswordStorageDescription => _protector.Description;

    public async Task<StoredSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync<SettingsFile>(stream, Options, cancellationToken).ConfigureAwait(false);
            if (file?.Account is null)
            {
                return null;
            }

            string? password = null;
            if (file.ProtectedPassword is { Length: > 0 })
            {
                if (file.PasswordScheme != _protector.Scheme)
                {
                    _logger.LogWarning("Stored password uses scheme {Scheme} which is not available here; it must be re-entered.", file.PasswordScheme);
                }
                else
                {
                    try
                    {
                        password = _protector.Unprotect(Convert.FromBase64String(file.ProtectedPassword));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stored password could not be decrypted; it must be re-entered.");
                    }
                }
            }

            return new StoredSettings(file.Account, password);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings from {Path}.", FilePath);
            return null;
        }
    }

    public async Task SaveAsync(SipAccountSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = new SettingsFile { Account = settings };
            if (!string.IsNullOrEmpty(password))
            {
                file.ProtectedPassword = Convert.ToBase64String(_protector.Protect(password));
                file.PasswordScheme = _protector.Scheme;
            }

            var tmp = FilePath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, file, Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, FilePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save settings to {Path}.", FilePath);
        }
    }

    private sealed class SettingsFile
    {
        public int Version { get; set; } = 1;
        public SipAccountSettings? Account { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? PasswordScheme { get; set; }
    }
}
