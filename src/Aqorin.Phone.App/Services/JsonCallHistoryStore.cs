using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

public sealed class JsonCallHistoryStore : ICallHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<JsonCallHistoryStore> _logger;

    public JsonCallHistoryStore(ILogger<JsonCallHistoryStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Aqorin.Phone"))
    {
    }

    public JsonCallHistoryStore(ILogger<JsonCallHistoryStore> logger, string directory)
    {
        _logger = logger;
        Directory = directory;
        FilePath = Path.Combine(directory, "call-history.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    public async Task<IReadOnlyList<CallHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Array.Empty<CallHistoryEntry>();
            }

            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<List<CallHistoryEntry>>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read call history from {Path}.", FilePath);
            return Array.Empty<CallHistoryEntry>();
        }
    }

    public async Task SaveAsync(IEnumerable<CallHistoryEntry> calls, CancellationToken cancellationToken = default)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, calls.ToList(), Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save call history to {Path}.", FilePath);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear call history at {Path}.", FilePath);
        }

        return Task.CompletedTask;
    }
}
