namespace Aqorin.Phone.App.Services;

public sealed record UiPreferences(
    bool IsDarkTheme = false,
    bool ShowIncomingCallPreviews = true,
    bool PlayIncomingCallSound = true);

public interface IUiPreferencesStore
{
    Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default);
}
