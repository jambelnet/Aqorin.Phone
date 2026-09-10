using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

public sealed class JsonUiPreferencesStore : IUiPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<JsonUiPreferencesStore> _logger;

    public JsonUiPreferencesStore(ILogger<JsonUiPreferencesStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Aqorin.Phone"))
    {
    }

    public JsonUiPreferencesStore(ILogger<JsonUiPreferencesStore> logger, string directory)
    {
        _logger = logger;
        Directory = directory;
        FilePath = Path.Combine(directory, "ui-preferences.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UiPreferences();
            }

            await using var stream = File.OpenRead(FilePath);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            return new UiPreferences(
                IsDarkTheme: ReadBool(root, "isDarkTheme", defaultValue: false),
                ShowIncomingCallPreviews: ReadBool(root, "showIncomingCallPreviews", defaultValue: true),
                PlayIncomingCallSound: ReadBool(root, "playIncomingCallSound", defaultValue: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read UI preferences from {Path}.", FilePath);
            return new UiPreferences();
        }
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save UI preferences to {Path}.", FilePath);
        }
    }

    private static bool ReadBool(JsonElement root, string propertyName, bool defaultValue) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
}
