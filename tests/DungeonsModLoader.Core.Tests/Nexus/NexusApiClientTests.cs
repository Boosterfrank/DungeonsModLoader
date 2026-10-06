using System.Net;
using System.Text.Json;
using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class NexusApiClientTests
{
    private const string ValidateJson = """{"user_id":123,"key":"k","name":"Tester","is_premium?":false,"is_premium":true,"is_supporter":false,"email":"t@example.com","profile_url":"https://avatar"}""";

    private const string TrendingJson = """
        [{"mod_id":5,"name":"Cool Mod","summary":"Nice","description":"[b]hi[/b]","picture_url":"https://staticdelivery.nexusmods.com/mods/10391/images/5/5-1.png","mod_downloads":100,"mod_unique_downloads":80,"version":"1.2","endorsement_count":7,"created_timestamp":1700000000,"updated_timestamp":1700003600,"author":"Alice","uploaded_by":"alice","contains_adult_content":false,"status":"published","available":true,"category_id":2}]
        """;

    private const string FilesJson = """
        {"files":[{"file_id":10,"name":"Cool Mod","version":"1.0","category_id":4,"category_name":"OLD_VERSION","is_primary":false,"size_kb":100,"size_in_bytes":102400,"file_name":"Cool Mod-5-1-0.zip","uploaded_timestamp":1700000000,"mod_version":"1.0","description":"","changelog_html":null},
                  {"file_id":11,"name":"Cool Mod","version":"1.2","category_id":1,"category_name":"MAIN","is_primary":true,"size_kb":120,"size_in_bytes":122880,"file_name":"Cool Mod-5-1-2.zip","uploaded_timestamp":1700003600,"mod_version":"1.2","description":"","changelog_html":"<b>fixes</b>"}],
         "file_updates":[{"old_file_id":10,"new_file_id":11,"old_file_name":"Cool Mod-5-1-0.zip","new_file_name":"Cool Mod-5-1-2.zip","uploaded_timestamp":1700003600}]}
        """;

    private const string SearchJson = """
        {"data":{"mods":{"totalCount":3,"nodes":[{"modId":100,"uid":"1","name":"Dungeons GUI X","summary":"Visual tools","author":"Boosterfrank","version":"1.0.1","endorsements":0,"downloads":265,"pictureUrl":"https://staticdelivery.nexusmods.com/mods/10391/images/100/100.png","thumbnailUrl":"https://staticdelivery.nexusmods.com/mods/10391/images/thumbnails/100/100.png","createdAt":"2026-10-05T01:42:49Z","updatedAt":"2026-10-06T00:13:14Z","status":"published","adultContent":false,"directDownloadEnabled":false,"uploader":{"name":"Boosterfrank","memberId":1},"modCategory":{"categoryId":4,"name":"User Interface"}}]}}}
        """;

    private static (NexusApiClient Client, FakeHttpHandler Handler, FakeKeyAccessor Key, MemoryCache Cache) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond, string? apiKey = "test-key-1234567890")
    {
        var handler = new FakeHttpHandler((request, body) => Task.FromResult(respond(request, body)));
        var key = new FakeKeyAccessor { ApiKey = apiKey };
        var cache = new MemoryCache();
        var client = new NexusApiClient(new FakeHttpClientFactory(handler), key, cache, NullLogger<NexusApiClient>.Instance);
        return (client, handler, key, cache);
    }

    [Fact]
    public async Task Validate_sends_the_key_and_reads_the_user()
    {
        var (client, handler, _, _) = Create((_, _) => FakeHttpHandler.Json(ValidateJson, headers: h =>
        {
            h.Add("X-RL-Hourly-Limit", "100");
            h.Add("X-RL-Hourly-Remaining", "96");
            h.Add("X-RL-Hourly-Reset", "2030-02-01T12:00:00+00:00");
            h.Add("X-RL-Daily-Limit", "2500");
            h.Add("X-RL-Daily-Remaining", "2488");
            h.Add("X-RL-Daily-Reset", "2030-02-02 00:00:00 +0000");
        }));

        var user = await client.ValidateAsync("abc-key-1234567890");

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://api.nexusmods.com/v1/users/validate.json", request.RequestUri!.ToString());
        Assert.Equal("abc-key-1234567890", request.Headers.GetValues("apikey").Single());
        Assert.Equal("Tester", user.Name);
        Assert.True(user.IsPremium);
        Assert.Equal(123, user.UserId);
        Assert.NotNull(client.RateLimit);
        Assert.Equal(96, client.RateLimit!.HourlyRemaining);
        Assert.Equal(2488, client.RateLimit.DailyRemaining);
        Assert.Equal(new DateTimeOffset(2030, 2, 2, 0, 0, 0, TimeSpan.Zero), client.RateLimit.DailyReset);
    }

    [Fact]
    public async Task Trending_uses_v1_with_a_key_and_maps_fields()
    {
        var (client, handler, _, _) = Create((_, _) => FakeHttpHandler.Json(TrendingJson));

        var page = await client.GetListAsync(NexusListKind.Trending);

        Assert.Contains("/v1/games/minecraftdungeons2/mods/trending.json", handler.Requests.Single().Request.RequestUri!.ToString());
        var mod = Assert.Single(page.Items);
        Assert.Equal(5, mod.ModId);
        Assert.Equal("Cool Mod", mod.Name);
        Assert.Equal("Alice", mod.Author);
        Assert.Equal(7, mod.Endorsements);
        Assert.Equal("https://staticdelivery.nexusmods.com/mods/10391/images/thumbnails/5/5-1.png", mod.ThumbnailUrl);
        Assert.False(page.HasMore);

        // Second call is served from the cache.
        await client.GetListAsync(NexusListKind.Trending);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Lists_without_a_key_use_graphql_and_page()
    {
        var (client, handler, _, _) = Create((_, _) => FakeHttpHandler.Json(SearchJson), apiKey: null);

        var page = await client.GetListAsync(NexusListKind.LatestAdded, offset: 0, count: 1);

        var (request, body) = handler.Requests.Single();
        Assert.Equal(NexusConstants.GraphQlUrl, request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("apikey"));
        using var json = JsonDocument.Parse(body!);
        var variables = json.RootElement.GetProperty("variables");
        Assert.Equal("minecraftdungeons2", variables.GetProperty("filter").GetProperty("filter")[0].GetProperty("gameDomainName")[0].GetProperty("value").GetString());
        Assert.Equal("DESC", variables.GetProperty("sort")[0].GetProperty("createdAt").GetProperty("direction").GetString());
        Assert.Equal(1, variables.GetProperty("count").GetInt32());
        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal("User Interface", page.Items[0].Category);
    }

    [Fact]
    public async Task Search_filters_by_name_and_sorts_by_relevance()
    {
        var (client, handler, _, _) = Create((_, _) => FakeHttpHandler.Json(SearchJson));

        await client.SearchAsync("gui");

        using var json = JsonDocument.Parse(handler.Requests.Single().Body!);
        var variables = json.RootElement.GetProperty("variables");
        var filters = variables.GetProperty("filter").GetProperty("filter");
        Assert.Equal(2, filters.GetArrayLength());
        Assert.Equal("gui", filters[1].GetProperty("name")[0].GetProperty("value").GetString());
        Assert.Equal("WILDCARD", filters[1].GetProperty("name")[0].GetProperty("op").GetString());
        Assert.True(variables.GetProperty("sort")[0].TryGetProperty("relevance", out _));
        Assert.Equal("test-key-1234567890", handler.Requests.Single().Request.Headers.GetValues("apikey").Single());
    }

    [Fact]
    public async Task Files_via_v1_include_update_chains_and_categories()
    {
        var (client, _, _, _) = Create((_, _) => FakeHttpHandler.Json(FilesJson));

        var files = await client.GetFilesAsync(5);

        Assert.Equal(2, files.Files.Count);
        Assert.Equal(NexusFileCategory.OldVersion, files.Files[0].Category);
        Assert.Equal(NexusFileCategory.Main, files.Files[1].Category);
        Assert.Equal(122880, files.Files[1].SizeBytes);
        Assert.Equal("Cool Mod-5-1-2.zip", files.Files[1].FileName);
        var update = Assert.Single(files.Updates);
        Assert.Equal(10, update.OldFileId);
        Assert.Equal(11, update.NewFileId);
    }

    [Fact]
    public async Task Unauthorized_becomes_an_auth_exception()
    {
        var (client, _, _, _) = Create((_, _) => FakeHttpHandler.Json("""{"message":"Please provide a valid API Key"}""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<NexusAuthException>(() => client.ValidateAsync("bad-key-1234567890"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Please provide a valid API Key", ex.ServerMessage);
        Assert.DoesNotContain("Please provide", ex.Message);
    }

    [Fact]
    public async Task Exhausted_rate_limit_stops_requests_until_the_reset()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);
        var (client, handler, _, _) = Create((_, _) => FakeHttpHandler.Json("""{"message":"too many"}""", HttpStatusCode.TooManyRequests, h =>
        {
            h.Add("X-RL-Hourly-Limit", "100");
            h.Add("X-RL-Hourly-Remaining", "0");
            h.Add("X-RL-Hourly-Reset", reset.ToString("o"));
            h.Add("X-RL-Daily-Limit", "2500");
            h.Add("X-RL-Daily-Remaining", "0");
        }));

        var first = await Assert.ThrowsAsync<NexusRateLimitException>(() => client.GetModAsync(5));
        Assert.NotNull(first.ResumeAt);
        Assert.Contains("minute", first.Message);
        Assert.Single(handler.Requests);

        // The client remembers the exhausted budget and does not even try again.
        await Assert.ThrowsAsync<NexusRateLimitException>(() => client.GetFilesAsync(5));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Burst_429_without_exhausted_headers_is_retried()
    {
        var calls = 0;
        var (client, _, _, _) = Create((_, _) => ++calls == 1
            ? FakeHttpHandler.Json("", HttpStatusCode.TooManyRequests)
            : FakeHttpHandler.Json(FilesJson));

        var files = await client.GetFilesAsync(5);

        Assert.Equal(2, calls);
        Assert.Equal(2, files.Files.Count);
    }

    [Fact]
    public async Task Download_links_need_premium_or_a_website_token()
    {
        var (client, handler, _, _) = Create((request, _) => request.RequestUri!.Query.Contains("key=")
            ? FakeHttpHandler.Json("""[{"name":"Nexus Global Content Delivery Network","short_name":"Nexus CDN","URI":"https://cf-files.nexusmods.com/cdn/10391/5/Cool%20Mod-5-1-2.zip?md5=x"}]""")
            : FakeHttpHandler.Json("""{"message":"premium only"}""", HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<NexusPremiumRequiredException>(() => client.GetDownloadLinksAsync(5, 11));

        var links = await client.GetDownloadLinksAsync(5, 11, "tok", 1800000000);

        var link = Assert.Single(links);
        Assert.Equal("Nexus CDN", link.ShortName);
        Assert.Contains("key=tok&expires=1800000000", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Stale_cache_is_served_when_nexus_is_unreachable()
    {
        var calls = 0;
        var (client, _, _, cache) = Create((_, _) =>
        {
            calls++;
            return calls == 1 ? FakeHttpHandler.Json(TrendingJson) : throw new HttpRequestException("offline");
        });

        var fresh = await client.GetListAsync(NexusListKind.Trending);
        cache.Expire("v1:list:trending");

        var stale = await client.GetListAsync(NexusListKind.Trending);

        Assert.Equal(fresh.Items[0].ModId, stale.Items[0].ModId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unreachable_without_cache_is_reported_as_unavailable()
    {
        var (client, _, _, _) = Create((_, _) => throw new HttpRequestException("offline"));

        var ex = await Assert.ThrowsAsync<NexusUnavailableException>(() => client.GetModAsync(5));

        Assert.Contains("internet connection", ex.Message);
    }

    [Fact]
    public async Task Updated_mods_require_a_key()
    {
        var (client, _, _, _) = Create((_, _) => FakeHttpHandler.Json("[]"), apiKey: null);

        await Assert.ThrowsAsync<NexusAuthException>(() => client.GetUpdatedAsync(NexusUpdatePeriod.Month));
    }
}
