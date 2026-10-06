using System.Net;
using System.Net.Http.Headers;
using DungeonsModLoader.Core.Tests.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class HttpDownloadServiceTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>Serves <see cref="Payload"/> with Range support; optionally cuts the first full response short.</summary>
    private static FakeHttpHandler Server(bool cutFirstResponse)
    {
        var calls = 0;
        return new FakeHttpHandler((request, _) =>
        {
            calls++;
            var from = 0L;
            var partial = false;
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } f })
            {
                from = f;
                partial = true;
            }

            var slice = Payload.AsMemory((int)from).ToArray();
            if (cutFirstResponse && calls == 1)
            {
                slice = slice[..50_000];
            }

            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(slice),
            };
            // Announce the real length so a short body is detected and resumed.
            response.Content.Headers.ContentLength = cutFirstResponse && calls == 1 ? Payload.Length : slice.Length;
            if (partial)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
            }

            return Task.FromResult(response);
        });
    }

    [Fact]
    public async Task Downloads_to_the_folder_and_reports_progress()
    {
        using var temp = new TempGameRoot();
        var handler = Server(cutFirstResponse: false);
        var service = new HttpDownloadService(new FakeHttpClientFactory(handler), NullLogger<HttpDownloadService>.Instance);
        var reports = new List<DownloadProgress>();

        var path = await service.DownloadAsync(new Uri("https://cdn.example/files/Cool%20Mod-5-1-2.zip?md5=x"), temp.DataDirectory, progress: new Progress<DownloadProgress>(reports.Add));

        Assert.Equal(Path.Combine(temp.DataDirectory, "Cool Mod-5-1-2.zip"), path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(temp.DataDirectory, "*.part"));
        await Task.Delay(50);
        Assert.Contains(reports, r => r.Fraction is 1.0);
    }

    [Fact]
    public async Task Resumes_a_cut_download_with_a_range_request()
    {
        using var temp = new TempGameRoot();
        var handler = Server(cutFirstResponse: true);
        var service = new HttpDownloadService(new FakeHttpClientFactory(handler), NullLogger<HttpDownloadService>.Instance);

        var path = await service.DownloadAsync(new Uri("https://cdn.example/f.zip"), temp.DataDirectory, "mod.zip");

        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(50_000, handler.Requests[1].Request.Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task Expired_links_are_reported_without_retrying()
    {
        using var temp = new TempGameRoot();
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)));
        var service = new HttpDownloadService(new FakeHttpClientFactory(handler), NullLogger<HttpDownloadService>.Instance);

        await Assert.ThrowsAsync<NexusLinkExpiredException>(() => service.DownloadAsync(new Uri("https://cdn.example/f.zip"), temp.DataDirectory));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void File_names_come_from_the_url_path()
    {
        var service = new HttpDownloadService(new FakeHttpClientFactory(new FakeHttpHandler((_, _) => throw new InvalidOperationException())), NullLogger<HttpDownloadService>.Instance);

        Assert.Equal("Cool Mod-5-1-2.zip", service.FileNameFor(new Uri("https://cf-files.nexusmods.com/cdn/10391/5/Cool%20Mod-5-1-2.zip?md5=abc&expires=1")));
        Assert.Equal("fallback.7z", service.FileNameFor(new Uri("https://cdn.example/download"), "fallback.7z"));
    }
}
