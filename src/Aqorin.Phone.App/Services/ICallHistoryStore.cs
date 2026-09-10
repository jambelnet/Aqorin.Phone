using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.App.Services;

public sealed record CallHistoryEntry(string Name, string Number, CallDirection Direction, string When, string Status, bool IsMissed);

public interface ICallHistoryStore
{
    Task<IReadOnlyList<CallHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IEnumerable<CallHistoryEntry> calls, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
