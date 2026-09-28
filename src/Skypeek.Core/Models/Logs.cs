namespace Skypeek.Core.Models;

/// <summary>A CloudWatch Logs location for a container or an environment.</summary>
public sealed record LogSource(string Label, string LogGroup, string? StreamPrefix, string? Note = null)
{
    /// <summary>For an RDS log file (read with DownloadDBLogFilePortion instead of CloudWatch Logs).</summary>
    public string? DbInstance { get; init; }
    public string? DbLogFile { get; init; }
    public DateTime? LastWritten { get; init; }
    public long? Size { get; init; }

    public bool IsRdsFile => DbLogFile is not null;
    public bool IsUsable => LogGroup.Length > 0 || IsRdsFile;

    public override string ToString() => Label;
}

public sealed record LogEvent(DateTime Timestamp, string Stream, string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string StreamShort => Stream.Length > 40 ? "…" + Stream[^39..] : Stream;
}

public sealed record LogPage(IReadOnlyList<LogEvent> Events, string? NextToken);

/// <summary>Logs Elastic Beanstalk collected from one instance (tail = text, bundle = zip download link).</summary>
public sealed record EbLogFile(string InstanceId, DateTime SampledAt, string Url, bool IsBundle);

/// <summary>Why an instance is unhealthy, from EB enhanced health.</summary>
public sealed class EbInstanceHealth
{
    public string InstanceId { get; init; } = "";
    public string? HealthStatus { get; init; }
    public string? Color { get; init; }
    public List<string> Causes { get; init; } = [];
    public string? DeploymentStatus { get; init; }
    public string? VersionLabel { get; init; }

    // From EC2 (DescribeInstances).
    public string? InstanceType { get; init; }
    public string? AvailabilityZone { get; init; }
    public string? State { get; init; }
    public DateTime? LaunchTime { get; init; }
    public string? PrivateIp { get; init; }

    public string CauseText => Causes.Count == 0 ? "" : string.Join("; ", Causes);
}
