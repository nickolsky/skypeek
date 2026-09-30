using Avalonia.Controls;
using Avalonia.Interactivity;
using Skypeek.Core;
using Skypeek.Core.Models;
using Skypeek.Desktop.Infrastructure;

namespace Skypeek.Desktop.Views;

/// <summary>
/// "Analyze reach": can a source open a connection to a destination on a port? Computed locally in an instant
/// (<see cref="ReachabilityAnalyzer"/>); optionally confirmed by AWS Reachability Analyzer with the elevated key.
/// </summary>
public partial class ReachDialog : DialogWindow
{
    private static readonly KeyValuePair<string, string>[] PortPresets =
    [
        new("SSH 22", "22"), new("HTTP 80", "80"), new("HTTPS 443", "443"), new("PostgreSQL 5432", "5432"), new("MySQL 3306", "3306"),
        new("MSSQL 1433", "1433"), new("Redis 6379", "6379"), new("Redshift 5439", "5439"), new("RDP 3389", "3389"),
    ];

    private readonly AppSession _session;
    private ReachRequest? _last;

    public ReachDialog() : this(null!, null) { }

    public ReachDialog(AppSession session, string? fromKey, string? toKey = null)
    {
        InitializeComponent();
        _session = session;
        Icon = IconRenderer.WindowIcon();
        Presets.ItemsSource = PortPresets;
        if (session is null)
            return;
        var options = ReachEndpoints.All(session);
        From.Setup(session, options);
        To.Setup(session, options);
        To.Picked += option =>
        {
            if (option.Port is { } port)
                Port.Text = port.ToString();
        };
        if (fromKey is not null)
            From.Select(fromKey);
        if (toKey is not null)
            To.Select(toKey);
        Opened += (_, _) => (fromKey is null ? From : To).FocusInput();
    }

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string port })
        {
            Port.Text = port;
            Protocol.SelectedIndex = 0;
        }
    }

    private string ProtocolValue => (Protocol.SelectedItem as ComboBoxItem)?.Tag as string ?? "tcp";

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        var protocol = ProtocolValue;
        var port = 0;
        if (protocol is "tcp" or "udp" && (!int.TryParse(Port.Text, out port) || port is < 1 or > 65535))
        {
            ShowError("Enter a port between 1 and 65535.");
            return;
        }
        if (From.Selected is not { } from || To.Selected is not { } to)
        {
            ShowError("Pick where the connection starts and where it goes.");
            return;
        }
        var source = await from.Resolve();
        var destination = await to.Resolve();
        if (source is null || destination is null)
        {
            ShowError($"Could not find the address of {(source is null ? from.Title : to.Title)} (DNS name not resolvable from here?).");
            return;
        }
        var request = _last = new ReachRequest(source, destination, protocol, port);
        var result = ReachabilityAnalyzer.Analyze(request, _session.Network.Snapshot());
        VerdictPanel.IsVisible = true;
        VerdictDot.Fill = LevelToBrushConverter.For(result.Level);
        VerdictTitle.Text = result.Verdict switch
        {
            ReachVerdict.Reachable => "Reachable",
            ReachVerdict.Blocked => "Blocked",
            _ => "Probably reachable",
        };
        VerdictText.Text = result.Summary;
        RequestText.Text = request.Text;
        HopList.ItemsSource = result.Hops;
        AwsPanel.IsVisible = false;
        UpdateVerify(request);
    }

    private void ShowError(string text)
    {
        VerdictPanel.IsVisible = true;
        VerdictDot.Fill = LevelToBrushConverter.Critical;
        VerdictTitle.Text = "Cannot check";
        VerdictText.Text = text;
        RequestText.Text = "";
        HopList.ItemsSource = null;
    }

    /// <summary>AWS's analyzer needs both ends in one account and region: the source an interface, the destination an interface or an address.</summary>
    private void UpdateVerify(ReachRequest request)
    {
        var target = request.Source.TargetId is { } id ? _session.Settings.FindTarget(id) : null;
        string? why = target is null ? "The source must be an interface in a downloaded VPC."
            : target.ElevatedProfileName is not { Length: > 0 } ? "Needs an elevated key for the source's target (Settings → Accounts & regions)."
            : request.Protocol is not ("tcp" or "udp") ? "AWS checks TCP or UDP only."
            : request.Destination.InAws && request.Destination.TargetId != request.Source.TargetId ? "Both ends must be in the same account and region for AWS's check."
            : null;
        VerifyButton.IsEnabled = why is null;
        VerifyNote.Text = why ?? "Uses the elevated key after you approve; AWS charges $0.10 and Skypeek deletes what it creates.";
    }

    private async void OnVerify(object? sender, RoutedEventArgs e)
    {
        if (_last is not { } request || request.Source.TargetId is not { } id || _session.Settings.FindTarget(id) is not { } target)
            return;
        VerifyButton.IsEnabled = false;
        AwsPanel.IsVisible = true;
        AwsTitle.Text = "AWS Reachability Analyzer: waiting for your approval…";
        AwsText.Text = "";
        try
        {
            AwsTitle.Text = "AWS Reachability Analyzer: running (up to 3 minutes)…";
            var result = await _session.Gateway.VerifyReachAsync(target, request.Source.Interface!.Id, request.Destination.Interface?.Id,
                request.Destination.InAws ? null : request.Destination.Address.ToString(), request.Protocol, request.Port, CancellationToken.None);
            AwsTitle.Text = result.Summary;
            AwsText.Text = string.Join(Environment.NewLine, result.Explanations.Concat(result.Path.Count > 0 ? [$"path: {string.Join(" → ", result.Path)}"] : []));
        }
        catch (ElevationDeniedException)
        {
            AwsTitle.Text = "Not approved; nothing was sent to AWS.";
        }
        catch (Exception ex)
        {
            AwsTitle.Text = $"AWS Reachability Analyzer failed: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
            App.Current.AskToSignInIfNeeded(ex);
        }
        finally
        {
            VerifyButton.IsEnabled = true;
        }
    }

    private void OnOpenGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ReachHop { GroupId: { } group, TargetId: { } id } && _session.Settings.FindTarget(id) is { } target
            && Owner is MainWindow main)
            main.OpenNetwork(target, group);
    }

    private void OnOpenSubnet(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ReachHop { SubnetId: { } subnet, TargetId: { } id } && Owner is MainWindow main)
            main.OpenNetworkNode($"{id}:subnet:{subnet}");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Finish(false);
}
