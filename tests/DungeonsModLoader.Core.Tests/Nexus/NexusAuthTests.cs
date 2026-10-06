using System.Net;
using DungeonsModLoader.Core.Tests.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class DpapiKeyStoreTests
{
    [Fact]
    public async Task Round_trips_and_clears_the_key_without_storing_it_in_plain_text()
    {
        using var temp = new TempGameRoot();
        var file = Path.Combine(temp.DataDirectory, "nexus-apikey.bin");
        var store = new DpapiNexusApiKeyStore(file, NullLogger<DpapiNexusApiKeyStore>.Instance);
        Assert.Null(await store.LoadAsync());

        await store.SaveAsync("  my-very-secret-key-123  ");

        Assert.True(File.Exists(file));
        var raw = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain("my-very-secret-key-123", System.Text.Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("my-very-secret-key-123", System.Text.Encoding.Unicode.GetString(raw));
        Assert.Equal("my-very-secret-key-123", await store.LoadAsync());

        await store.ClearAsync();

        Assert.False(File.Exists(file));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Garbage_file_is_treated_as_no_key()
    {
        using var temp = new TempGameRoot();
        var file = Path.Combine(temp.DataDirectory, "nexus-apikey.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3, 4 });
        var store = new DpapiNexusApiKeyStore(file, NullLogger<DpapiNexusApiKeyStore>.Instance);

        Assert.Null(await store.LoadAsync());
    }
}

internal sealed class FakeNexusClient : INexusApiClient
{
    public Func<string, Task<NexusUser>> OnValidate { get; set; } = key => Task.FromResult(new NexusUser(1, "Tester", true, false, null, null));
    public Func<NexusListKind, NexusModPage>? OnList { get; set; }
    public Func<long, NexusFileList>? OnFiles { get; set; }
    public Func<IReadOnlyList<NexusUpdatedMod>>? OnUpdated { get; set; }
    public Func<long, NexusMod>? OnMod { get; set; }
    public int FilesCalls { get; private set; }
    public int UpdatedCalls { get; private set; }

    public NexusRateLimit? RateLimit => null;
    public event EventHandler<NexusRateLimit>? RateLimitChanged { add { } remove { } }
    public bool HasApiKey => true;

    public Task<NexusUser> ValidateAsync(string apiKey, CancellationToken cancellationToken = default) => OnValidate(apiKey);

    public Task<NexusModPage> GetListAsync(NexusListKind kind, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(OnList?.Invoke(kind) ?? NexusModPage.Empty);

    public Task<NexusModPage> SearchAsync(string query, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(NexusModPage.Empty);

    public Task<NexusMod> GetModAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(OnMod?.Invoke(modId) ?? throw new NexusApiException(HttpStatusCode.NotFound, "missing"));

    public Task<NexusFileList> GetFilesAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        FilesCalls++;
        return Task.FromResult(OnFiles?.Invoke(modId) ?? NexusFileList.Empty);
    }

    public Task<IReadOnlyList<NexusRequirement>> GetRequirementsAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NexusRequirement>>(Array.Empty<NexusRequirement>());

    public Task<IReadOnlyList<NexusUpdatedMod>> GetUpdatedAsync(NexusUpdatePeriod period, bool refresh = false, CancellationToken cancellationToken = default)
    {
        UpdatedCalls++;
        return Task.FromResult(OnUpdated?.Invoke() ?? Array.Empty<NexusUpdatedMod>());
    }

    public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(long modId, long fileId, string? nxmKey = null, long? nxmExpires = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NexusDownloadLink>>(new[] { new NexusDownloadLink("CDN", "CDN", new Uri("https://cdn.example/f.zip")) });
}

internal sealed class MemoryKeyStore : INexusApiKeyStore
{
    public string? Key { get; set; }

    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Key);

    public Task SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        Key = apiKey;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Key = null;
        return Task.CompletedTask;
    }
}

public class NexusSessionTests
{
    private static (NexusSession Session, FakeNexusClient Client, MemoryKeyStore Store, NexusApiKeyHolder Holder) Create(string? storedKey = null)
    {
        var client = new FakeNexusClient();
        var store = new MemoryKeyStore { Key = storedKey };
        var holder = new NexusApiKeyHolder();
        var session = new NexusSession(client, store, holder, new INexusAuthProvider[] { new PersonalApiKeyAuthProvider(), new SsoAuthProvider(new NoBrowser(), NullLogger<SsoAuthProvider>.Instance) }, NullLogger<NexusSession>.Instance);
        return (session, client, store, holder);
    }

    private sealed class NoBrowser : IUrlOpener
    {
        public void OpenUrl(string url)
        {
        }
    }

    [Fact]
    public async Task Restore_without_a_key_is_logged_out_and_offers_only_the_personal_key_provider()
    {
        var (session, _, _, holder) = Create();

        await session.RestoreAsync();

        Assert.Equal(NexusSessionStatus.LoggedOut, session.Status);
        Assert.False(session.HasApiKey);
        Assert.Null(holder.ApiKey);
        Assert.Equal(NexusAuthMethod.PersonalApiKey, Assert.Single(session.Providers).Method);
    }

    [Fact]
    public async Task Login_with_a_personal_key_validates_stores_and_exposes_the_user()
    {
        var (session, _, store, holder) = Create();
        var changes = 0;
        session.Changed += (_, _) => changes++;

        var user = await session.LoginAsync(session.Providers[0], "  abcdefghijklmnopqrstuvwxyz  ");

        Assert.Equal("Tester", user.Name);
        Assert.Equal(NexusSessionStatus.LoggedIn, session.Status);
        Assert.True(session.IsPremium);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz", store.Key);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz", holder.ApiKey);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Rejected_key_is_reported_and_nothing_is_stored()
    {
        var (session, client, store, holder) = Create();
        client.OnValidate = _ => throw new NexusAuthException(HttpStatusCode.Unauthorized, "rejected");

        await Assert.ThrowsAsync<NexusAuthException>(() => session.LoginAsync(session.Providers[0], "abcdefghijklmnopqrstuvwxyz"));

        Assert.Equal(NexusSessionStatus.LoggedOut, session.Status);
        Assert.Equal("rejected", session.LastError);
        Assert.Null(store.Key);
        Assert.Null(holder.ApiKey);
        await Assert.ThrowsAsync<NexusException>(() => session.LoginAsync(session.Providers[0], "short"));
    }

    [Fact]
    public async Task Stored_key_that_cannot_be_checked_is_kept_as_unverified()
    {
        var (session, client, store, holder) = Create("stored-key-1234567890");
        client.OnValidate = _ => throw new NexusUnavailableException("offline");

        await session.RestoreAsync();

        Assert.Equal(NexusSessionStatus.Unverified, session.Status);
        Assert.True(session.HasApiKey);
        Assert.Equal("stored-key-1234567890", holder.ApiKey);
        Assert.Equal("stored-key-1234567890", store.Key);

        client.OnValidate = _ => Task.FromResult(new NexusUser(2, "Back", false, false, null, null));
        var user = await session.RevalidateAsync();
        Assert.Equal("Back", user!.Name);
        Assert.Equal(NexusSessionStatus.LoggedIn, session.Status);
        Assert.False(session.IsPremium);
    }

    [Fact]
    public async Task Stored_key_that_was_revoked_is_removed()
    {
        var (session, client, store, _) = Create("stored-key-1234567890");
        client.OnValidate = _ => throw new NexusAuthException(HttpStatusCode.Unauthorized, "revoked");

        await session.RestoreAsync();

        Assert.Equal(NexusSessionStatus.LoggedOut, session.Status);
        Assert.Null(store.Key);
        Assert.Contains("no longer accepts", session.LastError);
    }

    [Fact]
    public async Task Logout_removes_the_key()
    {
        var (session, _, store, holder) = Create("stored-key-1234567890");
        await session.RestoreAsync();
        Assert.Equal(NexusSessionStatus.LoggedIn, session.Status);

        await session.LogoutAsync();

        Assert.Equal(NexusSessionStatus.LoggedOut, session.Status);
        Assert.Null(store.Key);
        Assert.Null(holder.ApiKey);
        Assert.Null(session.User);
    }
}

public class SsoAuthProviderTests
{
    private sealed class ScriptedSocket : IWebSocketConnection
    {
        private readonly Queue<string?> _replies;

        public ScriptedSocket(IEnumerable<string?> replies)
        {
            _replies = new Queue<string?>(replies);
        }

        public List<string> Sent { get; } = new();

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingBrowser : IUrlOpener
    {
        public List<string> Urls { get; } = new();

        public void OpenUrl(string url) => Urls.Add(url);
    }

    [Fact]
    public void Parses_the_three_message_kinds()
    {
        var token = SsoAuthProvider.Parse("""{"success":true,"data":{"connection_token":"X5Sj"},"error":null}""");
        Assert.True(token.Success);
        Assert.Equal("X5Sj", token.ConnectionToken);
        Assert.Null(token.ApiKey);

        var key = SsoAuthProvider.Parse("""{"success":true,"data":{"api_key":"VX2P"},"error":null}""");
        Assert.Equal("VX2P", key.ApiKey);

        var error = SsoAuthProvider.Parse("""{"success":false,"data":null,"error":"nope"}""");
        Assert.False(error.Success);
        Assert.Equal("nope", error.Error);
    }

    [Fact]
    public async Task Is_unavailable_until_nexus_issues_a_slug()
    {
        var provider = new SsoAuthProvider(new RecordingBrowser(), NullLogger<SsoAuthProvider>.Instance, _ => new ScriptedSocket(Array.Empty<string?>()));

        Assert.False(provider.IsAvailable);
        await Assert.ThrowsAsync<NexusException>(() => provider.AcquireApiKeyAsync(null));
    }
}
