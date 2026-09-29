using System.Reflection;

namespace Skypeek.Desktop.Infrastructure;

/// <summary>Display name and version shown in window headers.</summary>
public static class AppInfo
{
    public const string Name = "Skypeek";

    /// <summary>e.g. "1.0.0", from &lt;Version&gt; in the project file.</summary>
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>"Skypeek for AWS 1.0.0": the name alone is ours; "for AWS" only says what it works with.</summary>
    public static string Title => $"{Name} for AWS {Version}";
}
