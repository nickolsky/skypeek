using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Skypeek.Core.Credentials;

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

    public static IReadOnlyDictionary<string, ProfileCredentials> ReadFile(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, ProfileCredentials>();

        // The file can be mid-write by the AWS Toolkit / CLI; retry briefly and allow shared access.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static IReadOnlyDictionary<string, ProfileCredentials> Parse(string content)
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

    public static string ComputeFingerprint(string accessKeyId, string secretAccessKey, string? sessionToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{accessKeyId}|{secretAccessKey}|{sessionToken}"));
        return Convert.ToHexString(bytes);
    }
}
