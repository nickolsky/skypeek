using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skypeek.Core.Credentials;

/// <summary>
/// An IAM Identity Center (AWS SSO) profile from ~/.aws/config (written by <c>aws configure sso</c>). The access token
/// comes from the CLI cache (<c>aws sso login</c>) and is exchanged for role credentials on use; it stays in memory.
/// </summary>
public sealed class SsoProfile
{
    public required string StartUrl { get; init; }
    public required string Region { get; init; }
    public required string AccountId { get; init; }
    public required string RoleName { get; init; }
    /// <summary>The [sso-session] name, or null for the older per-profile sso_start_url layout.</summary>
    public string? SessionName { get; init; }
    public required string TokenCacheFile { get; init; }
    public string? AccessToken { get; init; }
    public DateTime? TokenExpiresUtc { get; init; }
    /// <summary>
    /// The cached sign-in can be renewed without the browser (newer sso-session sign-ins keep a refresh token, which
    /// only the AWS CLI uses; Skypeek only notes that it is there).
    /// </summary>
    public bool CanRenew { get; init; }

    public bool IsSignedIn(DateTime nowUtc) => AccessToken is not null && (TokenExpiresUtc is null || TokenExpiresUtc > nowUtc.AddMinutes(1));

    /// <summary>Never includes the token.</summary>
    public override string ToString() => $"SSO {StartUrl} {AccountId}/{RoleName}";
}

/// <summary>Credentials of one profile. Secrets stay in memory only and are never logged.</summary>
public sealed class ProfileCredentials
{
    public required string Name { get; init; }
    public string? AccountId { get; init; }
    public string? RoleName { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public string? SessionToken { get; init; }
    public required string Fingerprint { get; init; }
    public string? DefaultRegion { get; init; }
    /// <summary>Set for SSO profiles; their keys come from sso:GetRoleCredentials, so the key fields are empty.</summary>
    public SsoProfile? Sso { get; init; }

    public bool IsSso => Sso is not null;
    public string SourceText => IsSso ? "SSO" : "credentials file";

    public bool IsReadOnly => RoleName is not null && RoleName.Contains("ReadOnly", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

public static partial class ProfileReader
{
    [GeneratedRegex(@"^(\d{12})_(.+)$")]
    private static partial Regex AccountRolePattern();

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");

    public static string DefaultConfigPath =>
        Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config");

    /// <summary>Where <c>aws sso login</c> caches access tokens.</summary>
    public static string DefaultSsoCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "sso", "cache");

    public static IReadOnlyDictionary<string, ProfileCredentials> ReadFile(string path) =>
        ReadText(path) is { } text ? Parse(text) : new Dictionary<string, ProfileCredentials>();

    /// <summary>
    /// Profiles from the credentials file plus SSO profiles from the config file. A name in both uses SSO while its
    /// sign-in is valid (it refreshes itself) and the credentials-file keys otherwise; callers re-read regularly, so the
    /// choice follows sign-in and sign-out.
    /// </summary>
    public static IReadOnlyDictionary<string, ProfileCredentials> ReadAll(string credentialsPath, string? configPath, string? ssoCacheDirectory,
        DateTime? nowUtc = null)
    {
        var result = new Dictionary<string, ProfileCredentials>(ReadFile(credentialsPath), StringComparer.Ordinal);
        if (configPath is null || ssoCacheDirectory is null || ReadText(configPath) is not { } config)
            return result;
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var (name, profile) in ParseSso(config, ssoCacheDirectory))
            if (!result.ContainsKey(name) || profile.Sso!.IsSignedIn(now))
                result[name] = profile;
        return result;
    }

    /// <summary>The file can be mid-write by the AWS Toolkit / CLI; retry briefly and allow shared access.</summary>
    private static string? ReadText(string path)
    {
        if (!File.Exists(path))
            return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// SSO profiles (sso_account_id + sso_role_name), with the start URL and region from their [sso-session] or from the
    /// profile itself (older layout). The fingerprint is the hash of the cached token, so a new <c>aws sso login</c>
    /// looks like updated credentials and resumes a halted profile.
    /// </summary>
    public static IReadOnlyDictionary<string, ProfileCredentials> ParseSso(string configContent, string ssoCacheDirectory)
    {
        var sections = ParseSections(configContent);
        var result = new Dictionary<string, ProfileCredentials>(StringComparer.Ordinal);
        foreach (var (name, values) in sections)
        {
            if (name.StartsWith("sso-session ", StringComparison.Ordinal)
                || !values.TryGetValue("sso_account_id", out var accountId) || !values.TryGetValue("sso_role_name", out var roleName))
                continue;

            values.TryGetValue("sso_session", out var sessionName);
            var session = sessionName is null ? values : sections.GetValueOrDefault($"sso-session {sessionName}");
            if (session is null || !session.TryGetValue("sso_start_url", out var startUrl) || !session.TryGetValue("sso_region", out var ssoRegion))
                continue;

            // The CLI names the cache file after the SHA-1 of the session name (or of the start URL for the old layout).
            var cacheKey = sessionName ?? startUrl;
            var tokenFile = Path.Combine(ssoCacheDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant() + ".json");
            var (token, expires, renewable) = ReadToken(tokenFile);
            values.TryGetValue("region", out var region);

            result[name] = new ProfileCredentials
            {
                Name = name,
                AccountId = accountId,
                RoleName = roleName,
                AccessKeyId = "",
                SecretAccessKey = "",
                Fingerprint = token is null ? $"SSO-NO-TOKEN:{cacheKey}" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"sso|{token}"))),
                DefaultRegion = region,
                Sso = new SsoProfile
                {
                    StartUrl = startUrl,
                    Region = ssoRegion,
                    AccountId = accountId,
                    RoleName = roleName,
                    SessionName = sessionName,
                    TokenCacheFile = tokenFile,
                    AccessToken = token,
                    TokenExpiresUtc = expires,
                    CanRenew = renewable && sessionName is not null,
                },
            };
        }
        return result;
    }

    /// <summary>
    /// The CLI rewrites the cache file when it refreshes a sign-in; a half-written file must not look like a sign-out
    /// (that would switch the profile to other keys), so unreadable JSON is retried briefly.
    /// </summary>
    private static (string? Token, DateTime? ExpiresUtc, bool Renewable) ReadToken(string tokenFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ParseToken(ReadText(tokenFile));
            }
            catch (Exception ex) when (ex is JsonException or IOException && attempt < 5)
            {
                Thread.Sleep(150);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return (null, null, false);
            }
        }
    }

    private static (string? Token, DateTime? ExpiresUtc, bool Renewable) ParseToken(string? json)
    {
        if (json is null)
            return (null, null, false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var token = root.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        DateTime? expires = null;
        // "2026-09-29T20:00:00Z" (CLI v2) or "2026-09-29T20:00:00UTC" (older tools).
        if (root.TryGetProperty("expiresAt", out var e) && e.GetString() is { } text
            && DateTime.TryParse(text.Replace("UTC", "Z"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            expires = parsed;
        // Only whether a refresh token is there (and its client registration still valid); its value is never read.
        var renewable = root.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String && !r.ValueEquals(string.Empty)
            && !(root.TryGetProperty("registrationExpiresAt", out var re) && re.GetString() is { } reText
                 && DateTime.TryParse(reText.Replace("UTC", "Z"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var regExpires)
                 && regExpires <= DateTime.UtcNow);
        return (string.IsNullOrEmpty(token) ? null : token, expires, renewable);
    }

    /// <summary>The raw lines of one [sso-session] section (start URL, region, scopes: no secrets), or null.</summary>
    public static string? SsoSessionSection(string configPath, string sessionName)
    {
        if (ReadText(configPath) is not { } content)
            return null;
        var lines = new List<string>();
        var inside = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = line[1..^1].Trim() == $"sso-session {sessionName}";
                if (inside)
                    lines.Add($"[sso-session {sessionName}]");
                continue;
            }
            if (inside && line.Length > 0 && line[0] is not ('#' or ';'))
                lines.Add(line);
        }
        return lines.Count > 1 ? string.Join('\n', lines) : null;
    }

    public static IReadOnlyDictionary<string, ProfileCredentials> Parse(string content)
    {
        var sections = ParseSections(content);
        var result = new Dictionary<string, ProfileCredentials>(StringComparer.Ordinal);
        foreach (var (name, values) in sections)
        {
            if (!values.TryGetValue("aws_access_key_id", out var keyId) || !values.TryGetValue("aws_secret_access_key", out var secret))
                continue;
            values.TryGetValue("aws_session_token", out var token);
            values.TryGetValue("region", out var region);

            var match = AccountRolePattern().Match(name);
            result[name] = new ProfileCredentials
            {
                Name = name,
                AccountId = match.Success ? match.Groups[1].Value : null,
                RoleName = match.Success ? match.Groups[2].Value : null,
                AccessKeyId = keyId,
                SecretAccessKey = secret,
                SessionToken = string.IsNullOrEmpty(token) ? null : token,
                Fingerprint = ComputeFingerprint(keyId, secret, token),
                DefaultRegion = region,
            };
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (name.StartsWith("profile ", StringComparison.Ordinal))
                    name = name["profile ".Length..].Trim();
                current = sections.TryGetValue(name, out var existing) ? existing : sections[name] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0)
                continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    public static string ComputeFingerprint(string accessKeyId, string secretAccessKey, string? sessionToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{accessKeyId}|{secretAccessKey}|{sessionToken}"));
        return Convert.ToHexString(bytes);
    }
}
