using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>
/// Collects one security group rule (add, or the new values of an existing rule). Nothing is sent from here: the
/// gateway validates it again and the permission dialog shows the exact change.
/// </summary>
public partial class RuleEditorDialog
{
    public sealed record PresetOption(string Label, string? Protocol, int? From, int? To);
    public sealed record SourceOption(string Label, string Value);

    private static readonly PresetOption[] Presets =
    [
        new("Custom TCP", "tcp", null, null),
        new("Custom UDP", "udp", null, null),
        new("All traffic", "-1", null, null),
        new("All TCP", "tcp", 0, 65535),
        new("All UDP", "udp", 0, 65535),
        new("All ICMP (IPv4)", "icmp", -1, -1),
        new("All ICMPv6", "icmpv6", -1, -1),
        new("SSH (22)", "tcp", 22, 22),
        new("RDP (3389)", "tcp", 3389, 3389),
        new("HTTP (80)", "tcp", 80, 80),
        new("HTTPS (443)", "tcp", 443, 443),
        new("PostgreSQL (5432)", "tcp", 5432, 5432),
        new("MySQL / Aurora (3306)", "tcp", 3306, 3306),
        new("MSSQL (1433)", "tcp", 1433, 1433),
        new("Redis (6379)", "tcp", 6379, 6379),
        new("Custom protocol", null, null, null),
    ];

    private static readonly HttpClient CheckIp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly bool _isEgress;
    private readonly IReadOnlyList<SecurityGroupInfo> _groups;
    private readonly IReadOnlyList<string> _vpcCidrs;
    private bool _loading = true;

    /// <param name="existing">The rule being edited (its direction stays), or null to add one.</param>
    /// <param name="vpcGroups">Groups of the same VPC, offered as sources.</param>
    public RuleEditorDialog(SecurityGroupInfo group, SecurityGroupRuleInfo? existing, bool isEgress, IReadOnlyList<SecurityGroupInfo> vpcGroups, IReadOnlyList<string> vpcCidrs)
    {
        InitializeComponent();
        _isEgress = existing?.IsEgress ?? isEgress;
        _groups = vpcGroups;
        _vpcCidrs = vpcCidrs;
        GroupText.Text = $"{group.Name} ({group.Id})";
        CaptionText.Text = existing is null ? $"Add {(_isEgress ? "outbound" : "inbound")} rule" : $"Edit rule {existing.RuleId}";
        DirectionText.Text = _isEgress
            ? "Outbound: which destinations resources in this group may open connections to."
            : "Inbound: who may connect to resources in this group.";
        SourceLabel.Text = _isEgress ? "Destination" : "Source";
        Preset.ItemsSource = Presets;

        var spec = existing?.ToSpec();
        if (spec is null)
        {
            Preset.SelectedIndex = Array.FindIndex(Presets, p => p.Label.StartsWith("HTTPS", StringComparison.Ordinal));
            SourceKind.SelectedIndex = 0;
        }
        else
        {
            var match = Array.FindIndex(Presets, p => p.Protocol == spec.Protocol && p.From == spec.FromPort && p.To == spec.ToPort && p.From is not null);
            Preset.SelectedIndex = match >= 0 ? match
                : spec.Protocol == "-1" ? Array.FindIndex(Presets, p => p.Protocol == "-1")
                : spec.Protocol == "tcp" ? 0 : spec.Protocol == "udp" ? 1 : Presets.Length - 1;
            Protocol.Text = spec.Protocol;
            Ports.Text = spec.FromPort is null ? "" : spec.FromPort == spec.ToPort ? $"{spec.FromPort}" : $"{spec.FromPort}-{spec.ToPort}";
            SourceKind.SelectedIndex = (int)spec.SourceKind;
            Source.Text = spec.Source;
            Description.Text = spec.Description ?? "";
        }
        _loading = false;
        UpdateSourceOptions(keepText: existing is not null);
        UpdatePreview();
        SourceInitialized += (_, _) => ConfirmDialog.RoundCorners(this);
        Loaded += (_, _) => (existing is null ? Source : (Control)Ports).Focus();
    }

    public SecurityGroupRuleSpec? Result { get; private set; }

    private RuleSourceKind Kind => (RuleSourceKind)Math.Max(0, SourceKind.SelectedIndex);

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Preset.SelectedItem is not PresetOption p)
            return;
        if (p.Protocol is not null)
            Protocol.Text = p.Protocol;
        Protocol.IsEnabled = p.Protocol is null;
        if (p.From is not null)
            Ports.Text = p.From == p.To ? $"{p.From}" : $"{p.From}-{p.To}";
        else if (!_loading && p.Protocol == "-1")
            Ports.Text = "";
        UpdatePreview();
    }

    private void OnSourceKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        UpdateSourceOptions(keepText: false);
        UpdatePreview();
    }

    private void UpdateSourceOptions(bool keepText)
    {
        var text = Source.Text;
        List<SourceOption> options = Kind switch
        {
            RuleSourceKind.Ipv4 => [.. _vpcCidrs.Where(c => c.Contains('.')).Select(c => new SourceOption($"{c} (this VPC)", c)), new("0.0.0.0/0 (anywhere)", "0.0.0.0/0")],
            RuleSourceKind.Ipv6 => [new("::/0 (anywhere)", "::/0")],
            RuleSourceKind.SecurityGroup => _groups.Select(g => new SourceOption($"{g.Id} ({g.Name})", g.Id)).ToList(),
            _ => [],
        };
        Source.ItemsSource = options;
        Source.Text = keepText ? text : "";
        MyIpButton.Visibility = Kind == RuleSourceKind.Ipv4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            UpdatePreview();
    }

    private async void OnMyIp(object sender, RoutedEventArgs e)
    {
        MyIpButton.IsEnabled = false;
        try
        {
            var ip = (await CheckIp.GetStringAsync("https://checkip.amazonaws.com")).Trim();
            if (System.Net.IPAddress.TryParse(ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                Source.Text = $"{ip}/32";
            else
                ShowError("checkip.amazonaws.com returned something unexpected.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowError($"Could not look up your public IP: {ex.Message}");
        }
        finally
        {
            MyIpButton.IsEnabled = true;
        }
    }

    /// <summary>The value typed or picked; picked entries show "value (label)".</summary>
    private string SourceValue()
    {
        if (Source.SelectedItem is SourceOption picked && Source.Text == picked.Label)
            return picked.Value;
        var text = Source.Text.Trim();
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    private SecurityGroupRuleSpec? Build(out string? error)
    {
        var protocol = Protocol.Text.Trim().ToLowerInvariant() switch
        {
            "all" => "-1",
            var p => p,
        };
        int? from = null, to = null;
        var ports = Ports.Text.Trim();
        if (protocol != "-1" && ports.Length > 0)
        {
            var parts = ports.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                error = protocol is "icmp" or "icmpv6" ? "Enter the ICMP type, or type-code (for example 8-0); -1 means all." : "Enter a port (443) or a range (8000-8100).";
                return null;
            }
            (from, to) = (a, b);
        }
        else if (protocol is "icmp" or "icmpv6")
        {
            (from, to) = (-1, -1);
        }
        var spec = new SecurityGroupRuleSpec(_isEgress, protocol, from, to, Kind, SourceValue(), string.IsNullOrWhiteSpace(Description.Text) ? null : Description.Text.Trim());
        error = NetworkRules.Validate(spec);
        return error is null ? spec : null;
    }

    private void UpdatePreview()
    {
        var spec = Build(out _);
        SummaryText.Text = spec is null ? "" : $"Rule: {spec.Summary}";
        WorldWarning.Visibility = spec is { IsOpenToWorld: true, IsEgress: false } ? Visibility.Visible : Visibility.Collapsed;
        PortLabel.Text = Protocol.Text.Trim() is "icmp" or "icmpv6" ? "Type-code" : "Port range";
        Ports.IsEnabled = Protocol.Text.Trim() is not ("-1" or "all");
        Error.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Build(out var error) is not { } spec)
        {
            ShowError(error ?? "Check the rule.");
            return;
        }
        Result = spec;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
