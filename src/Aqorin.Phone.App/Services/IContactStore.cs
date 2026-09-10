namespace Aqorin.Phone.App.Services;

public sealed record ContactEntry(string Name, string Number, bool IsFavorite = true);

public interface IContactStore
{
    Task<IReadOnlyList<ContactEntry>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IEnumerable<ContactEntry> contacts, CancellationToken cancellationToken = default);
}
