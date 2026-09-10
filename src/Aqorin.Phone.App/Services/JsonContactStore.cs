using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

public sealed class JsonContactStore : IContactStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ILogger<JsonContactStore> _logger;

    public JsonContactStore(ILogger<JsonContactStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Aqorin.Phone"))
    {
    }

    public JsonContactStore(ILogger<JsonContactStore> logger, string directory)
    {
        _logger = logger;
        Directory = directory;
        FilePath = Path.Combine(directory, "contacts.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    public async Task<IReadOnlyList<ContactEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Array.Empty<ContactEntry>();
            }

            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<List<ContactEntry>>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read contacts from {Path}.", FilePath);
            return Array.Empty<ContactEntry>();
        }
    }

    public async Task SaveAsync(IEnumerable<ContactEntry> contacts, CancellationToken cancellationToken = default)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, contacts.ToList(), Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save contacts to {Path}.", FilePath);
        }
    }
}
