using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DungeonsModLoader.Core;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.Download;
using DungeonsModLoader.Nexus.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DungeonsModLoader.Nexus.DependencyInjection;

/// <summary>Registrations of the Nexus layer. The app must register an <see cref="IUrlOpener"/> for the SSO flow.</summary>
public static class NexusServiceCollectionExtensions
{
    /// <summary>"DungeonsModLoader/0.1.0 (Windows 10.0.26200; X64) (+https://github.com/...)" as the API docs ask for.</summary>
    public static string UserAgent { get; } =
        $"{AppInfo.DisplayName}/{AppInfo.Version} ({RuntimeInformation.OSDescription.Replace("Microsoft ", string.Empty)}; {RuntimeInformation.OSArchitecture}) (+https://github.com/{AppInfo.GitHubRepository})";

    /// <summary>Windows-only because the API key store uses DPAPI.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddNexus(this IServiceCollection services)
    {
        services.AddHttpClient(NexusHttpClients.Api, client =>
        {
            client.BaseAddress = new Uri(NexusConstants.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            AddIdentification(client);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddHttpClient(NexusHttpClients.Download, client =>
        {
            // Large files: the cancellation token, not a timeout, ends a download.
            client.Timeout = Timeout.InfiniteTimeSpan;
            AddIdentification(client);
        });

        services.TryAddSingleton<INexusCache, JsonDiskCache>();
        services.TryAddSingleton<IThumbnailCache, ThumbnailCache>();
        services.TryAddSingleton<INexusApiKeyStore, DpapiNexusApiKeyStore>();
        services.TryAddSingleton<NexusApiKeyHolder>();
        services.TryAddSingleton<INexusApiKeyAccessor>(provider => provider.GetRequiredService<NexusApiKeyHolder>());
        services.TryAddSingleton<INexusApiClient, NexusApiClient>();
        services.AddSingleton<INexusAuthProvider, PersonalApiKeyAuthProvider>();
        services.AddSingleton<INexusAuthProvider, SsoAuthProvider>();
        services.TryAddSingleton<INexusSession, NexusSession>();
        services.TryAddSingleton<IDownloadService, HttpDownloadService>();
        services.TryAddSingleton<IModUpdateChecker, ModUpdateChecker>();
        return services;
    }

    private static void AddIdentification(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", NexusConstants.ApplicationName);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", AppInfo.Version);
    }
}
