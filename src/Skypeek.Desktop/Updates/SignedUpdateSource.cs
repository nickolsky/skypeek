using System.Net.Http.Headers;
using System.Reflection;
using Skypeek.Core.Updates;
using Skypeek.Desktop.Infrastructure;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Skypeek.Desktop.Updates;

/// <summary>
/// Velopack update source that only accepts a feed signed with one of the keys built into the app
/// (<see cref="ReleaseSignature"/>). The feed lists each package's SHA-256, which Velopack checks after downloading,
/// so a replaced package fails as well. Packages download from the same base address.
/// </summary>
public sealed class SignedUpdateSource : IUpdateSource
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _baseUrl;
    private readonly IReadOnlyCollection<string> _trustedKeys;
    private readonly SimpleWebSource _packages;

    public SignedUpdateSource(string baseUrl, IReadOnlyCollection<string> trustedKeys)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _trustedKeys = trustedKeys;
        _packages = new SimpleWebSource(_baseUrl + "/", new HttpClientFileDownloader(), timeout: 300);
    }

    /// <summary>The update address and keys this build was made with (Skypeek.Desktop.csproj, UpdateKeys/).</summary>
    public static SignedUpdateSource? FromBuild()
    {
        var url = BuildSetting("SkypeekUpdateUrl");
        if (url is null || !IsAllowed(url))
            return null;
        return new SignedUpdateSource(url, BuiltInKeys());
    }

    public static string? BuildSetting(string name) =>
        typeof(SignedUpdateSource).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value is { Length: > 0 } value
            ? value
            : null;

    public static IReadOnlyCollection<string> BuiltInKeys()
    {
        var assembly = typeof(SignedUpdateSource).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("UpdateKeys.", StringComparison.Ordinal) && n.EndsWith(".pub.pem", StringComparison.Ordinal))
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            })
            .ToList();
    }

    public bool HasKeys => _trustedKeys.Count > 0;

    /// <summary>HTTPS only (plain HTTP just for a local test server).</summary>
    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var feedName = $"releases.{channel}.json";
        var feed = await GetBytesAsync(feedName).ConfigureAwait(false)
            ?? throw new UpdateCheckException($"no update feed for this platform ({feedName})");
        var signature = await GetBytesAsync(feedName + ReleaseSignature.Extension).ConfigureAwait(false);
        if (ReleaseSignature.Verify(_trustedKeys, feedName, feed, signature is null ? null : System.Text.Encoding.UTF8.GetString(signature)) is { } problem)
        {
            logger.LogWarning($"Update feed rejected: {problem}");
            throw new UpdateCheckException("update ignored: " + problem);
        }
        var parsed = VelopackAssetFeed.FromJson(System.Text.Encoding.UTF8.GetString(feed));
        if (appId is not null)
            parsed.Assets = parsed.Assets.Where(a => string.Equals(a.PackageId, appId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return parsed;
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
        _packages.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);

    /// <summary>The file's bytes, or null when the server says it is not there.</summary>
    private async Task<byte[]?> GetBytesAsync(string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/{name}");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
        return client;
    }
}

/// <summary>An update check that failed for a reason worth showing (no feed, bad signature).</summary>
public sealed class UpdateCheckException(string message) : Exception(message);
