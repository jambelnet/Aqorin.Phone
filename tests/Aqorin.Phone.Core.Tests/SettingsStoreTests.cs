using Aqorin.Phone.App.Services;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Aqorin.Phone.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private JsonSettingsStore Create(IPasswordProtector? protector = null) =>
        new(NullLogger<JsonSettingsStore>.Instance, _dir, protector ?? new UserKeyFilePasswordProtector(_dir));

    [Fact]
    public async Task Round_trips_settings_and_password_without_plaintext_on_disk()
    {
        var store = Create();
        var settings = new SipAccountSettings { Registrar = "fritz.box", Username = "620", DisplayName = "Desk", Transport = SipTransport.Tcp, RegistrationExpirySeconds = 600 };

        await store.SaveAsync(settings, "top-secret-pw");
        var json = await File.ReadAllTextAsync(store.FilePath);

        Assert.DoesNotContain("top-secret-pw", json);
        Assert.Contains("\"protectedPassword\"", json);
        Assert.Contains("aes-gcm-userkey", json);

        var loaded = await Create().LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(settings, loaded!.Settings);
        Assert.Equal("top-secret-pw", loaded.Password);
    }

    [Fact]
    public async Task Null_password_removes_a_previously_stored_one()
    {
        var store = Create();
        var settings = new SipAccountSettings { Username = "620" };
        await store.SaveAsync(settings, "pw");
        await store.SaveAsync(settings, null);

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("protectedPassword", json);
        var loaded = await store.LoadAsync();
        Assert.Null(loaded!.Password);
        Assert.Equal("620", loaded.Settings.Username);
    }

    [Fact]
    public async Task Password_protected_with_a_different_key_cannot_be_read_and_is_dropped_gracefully()
    {
        var store = Create();
        await store.SaveAsync(new SipAccountSettings { Username = "620" }, "pw");

        // Simulate another user/profile: a store with a different key file.
        var otherDir = Path.Combine(_dir, "other");
        var other = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _dir, new UserKeyFilePasswordProtector(otherDir));
        var loaded = await other.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Password);
        Assert.Equal("620", loaded.Settings.Username);
    }

    [Fact]
    public async Task Missing_file_returns_null()
    {
        Assert.Null(await Create().LoadAsync());
    }

    [Fact]
    public void Dpapi_protector_round_trips_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiPasswordProtector();
        var cipher = protector.Protect("pw-ü-🙂");
        Assert.DoesNotContain("pw-", System.Text.Encoding.UTF8.GetString(cipher));
        Assert.Equal("pw-ü-🙂", protector.Unprotect(cipher));
    }

    [Fact]
    public void Default_protector_matches_the_platform()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _dir, null);
        Assert.Contains(OperatingSystem.IsWindows() ? "DPAPI" : "AES-256-GCM", store.PasswordStorageDescription);
    }
}
