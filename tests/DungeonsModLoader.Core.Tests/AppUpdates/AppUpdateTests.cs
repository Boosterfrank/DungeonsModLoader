using System.Net;
using DungeonsModLoader.Core.AppUpdates;
using DungeonsModLoader.Core.Tests.Nexus;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.AppUpdates;

/// <summary>Version parsing, release-to-update resolution and the GitHub release client.</summary>
public class AppUpdateTests
{
    private static GitHubReleaseAsset Setup(string version) =>
        new($"DungeonsModLoader-Setup-{version}.exe", new Uri($"https://github.com/Boosterfrank/DungeonsModLoader/releases/download/v{version}/DungeonsModLoader-Setup-{version}.exe"), 55_000_000);

    private static GitHubRelease Release(string tag, bool prerelease = false, bool draft = false, params GitHubReleaseAsset[] assets) =>
        new(tag, tag, new Uri("https://github.com/Boosterfrank/DungeonsModLoader/releases/tag/" + tag), prerelease, draft, DateTimeOffset.UtcNow, "Notes", assets);

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("V1.0", 1, 0, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    [InlineData(" 2.0.0 ", 2, 0, 0)]
    public void Versions_parse_tags_and_strings(string text, int major, int minor, int build)
    {
        Assert.True(AppVersions.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, build), new Version(version.Major, version.Minor, version.Build));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.x")]
    public void Unparseable_versions_are_rejected(string text)
    {
        Assert.False(AppVersions.TryParse(text, out _));
        Assert.False(AppVersions.IsNewer(text, "0.1.0"));
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("v0.0.9", "0.1.0", false)]
    [InlineData("0.1.0", "0.1.0-beta", false)]
    public void IsNewer_compares_numerically(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, AppVersions.IsNewer(candidate, current));
    }

    [Fact]
    public void Resolve_returns_the_update_with_the_setup_asset()
    {
        var release = Release("v0.2.0", assets: [new GitHubReleaseAsset("Source code.zip", new Uri("https://example.test/src.zip"), 10), Setup("0.2.0")]);

        var update = AppUpdateResolver.Resolve(release, "0.1.0");

        Assert.NotNull(update);
        Assert.Equal("0.2.0", update.VersionText);
        Assert.Equal("DungeonsModLoader-Setup-0.2.0.exe", update.Installer.Name);
        Assert.Equal("Notes", update.Notes);
        Assert.Equal(release.HtmlUrl, update.ReleaseUrl);
    }

    [Fact]
    public void Resolve_ignores_same_or_older_versions_prereleases_and_drafts()
    {
        Assert.Null(AppUpdateResolver.Resolve(Release("v0.1.0", assets: Setup("0.1.0")), "0.1.0"));
        Assert.Null(AppUpdateResolver.Resolve(Release("v0.0.5", assets: Setup("0.0.5")), "0.1.0"));
        Assert.Null(AppUpdateResolver.Resolve(Release("v0.3.0", prerelease: true, assets: Setup("0.3.0")), "0.1.0"));
        Assert.Null(AppUpdateResolver.Resolve(Release("v0.3.0", draft: true, assets: Setup("0.3.0")), "0.1.0"));
        Assert.Null(AppUpdateResolver.Resolve(null, "0.1.0"));
    }

    [Fact]
    public void Resolve_needs_an_installer_asset()
    {
        var release = Release("v0.2.0", assets: new GitHubReleaseAsset("notes.txt", new Uri("https://example.test/notes.txt"), 1));

        Assert.Null(AppUpdateResolver.Resolve(release, "0.1.0"));
    }

    [Fact]
    public async Task Client_parses_the_latest_release()
    {
        const string json = """
            {
              "tag_name": "v0.2.0",
              "name": "DungeonsModLoader 0.2.0",
              "html_url": "https://github.com/Boosterfrank/DungeonsModLoader/releases/tag/v0.2.0",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-10-07T12:00:00Z",
              "body": "- Fixes",
              "assets": [
                { "name": "DungeonsModLoader-Setup-0.2.0.exe", "browser_download_url": "https://github.com/Boosterfrank/DungeonsModLoader/releases/download/v0.2.0/DungeonsModLoader-Setup-0.2.0.exe", "size": 12345 },
                { "name": "broken", "browser_download_url": "not a url", "size": 1 }
              ]
            }
            """;
        var handler = new FakeHttpHandler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/Boosterfrank/DungeonsModLoader/releases/latest", request.RequestUri!.ToString());
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
            Assert.True(request.Headers.UserAgent.Count > 0 || request.Headers.Contains("User-Agent"));
            return Task.FromResult(FakeHttpHandler.Json(json));
        });
        var client = new GitHubReleaseClient(() => CreateClient(handler), NullLogger<GitHubReleaseClient>.Instance);

        var release = await client.GetLatestReleaseAsync("Boosterfrank/DungeonsModLoader");

        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release.TagName);
        Assert.False(release.IsPrerelease);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), release.PublishedAt);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(12345, asset.Size);
        Assert.NotNull(AppUpdateResolver.Resolve(release, "0.1.0"));
    }

    [Fact]
    public async Task Client_returns_null_when_there_are_no_releases()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)));
        var client = new GitHubReleaseClient(() => CreateClient(handler), NullLogger<GitHubReleaseClient>.Instance);

        Assert.Null(await client.GetLatestReleaseAsync("Boosterfrank/DungeonsModLoader"));
    }

    [Fact]
    public async Task Client_reports_rate_limits_and_server_errors_in_plain_words()
    {
        var limited = new GitHubReleaseClient(() => CreateClient(new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("{}", HttpStatusCode.Forbidden)))), NullLogger<GitHubReleaseClient>.Instance);
        var error = await Assert.ThrowsAsync<AppUpdateException>(() => limited.GetLatestReleaseAsync("a/b"));
        Assert.Contains("limiting", error.Message);

        var broken = new GitHubReleaseClient(() => CreateClient(new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("{}", HttpStatusCode.InternalServerError)))), NullLogger<GitHubReleaseClient>.Instance);
        error = await Assert.ThrowsAsync<AppUpdateException>(() => broken.GetLatestReleaseAsync("a/b"));
        Assert.Contains("500", error.Message);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DungeonsModLoader/0.0.1 (test)");
        return client;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    public void Update_becomes_mandatory_after_two_outdated_starts(int outdatedStarts, bool mandatory)
    {
        Assert.Equal(mandatory, AppUpdatePolicy.IsMandatory(outdatedStarts));
    }
}
