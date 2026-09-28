using System.Text.Json;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Tests;

public class LogExportTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Target Target = new() { Id = 1, Alias = "CA", ProfileName = "p", Region = "ca-central-1" };

    private static readonly EbEnvironmentStatus Env = new()
    {
        TargetId = 1,
        Level = HealthLevel.Critical,
        Reasons = ["Health Red (Degraded)"],
        Snapshot = new EbEnvironmentSnapshot
        {
            EnvironmentName = "shop-prod-api",
            ApplicationName = "Shop-V1",
            Health = "Red",
            Causes = ["30 % of the requests are erroring with HTTP 5xx."],
            InstanceHealth = [new EbInstanceHealth { InstanceId = "i-1", Color = "Red", Causes = ["Process default has died."] }],
        },
        RecentEvents = [new EbEvent { Date = Now.AddMinutes(-5), Severity = "ERROR", Message = "Failed to deploy application." }],
    };

    private static readonly List<LogEvent> Lines =
    [
        new(Now.AddMinutes(-1), "web.stdout.log", "second"),
        new(Now.AddMinutes(-2), "web.stdout.log", "first"),
    ];

    private static LogExportContext Ctx => new(Target, "111122223333", Env, "CloudWatch Logs /aws/elasticbeanstalk/x", Now.AddHours(-1), Now, "ERROR");

    [Fact]
    public void Markdown_has_instruction_state_and_ordered_lines()
    {
        var md = LogExport.ToMarkdown(Ctx, Lines, Now);

        Assert.Contains("root cause", md);
        Assert.Contains("30 % of the requests are erroring", md);
        Assert.Contains("Process default has died.", md);
        Assert.Contains("Failed to deploy application.", md);
        Assert.Contains("account 111122223333", md);
        Assert.Contains("Filter pattern:** ERROR", md);
        Assert.True(md.IndexOf("first", StringComparison.Ordinal) < md.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_lines_are_valid_json_with_context_first()
    {
        var rows = LogExport.ToJsonLines(Ctx, Lines, Now).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, rows.Length);
        using var context = JsonDocument.Parse(rows[0]);
        Assert.Equal("context", context.RootElement.GetProperty("type").GetString());
        Assert.Contains(context.RootElement.GetProperty("current_state").EnumerateArray(), e => e.GetString()!.Contains("5xx"));
        using var first = JsonDocument.Parse(rows[1]);
        Assert.Equal("first", first.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void File_name_is_safe()
    {
        var name = LogExport.SuggestFileName(Ctx, Now, "md");
        Assert.StartsWith("CA-shop-prod-api-", name);
        Assert.EndsWith(".md", name);
        Assert.DoesNotContain(" ", name);
    }
}
