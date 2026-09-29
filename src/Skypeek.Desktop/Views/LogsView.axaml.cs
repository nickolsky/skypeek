using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

/// <summary>Open the logs of one container in one ECS task.</summary>
public sealed record LogFocus(string Container, string TaskId);

/// <summary>
/// CloudWatch Logs for an ECS service's containers, an EB environment or an RDS instance, EB instance log requests,
/// and RDS log files (history and live tail).
/// </summary>
public partial class LogsView : UserControl
{
    public sealed record RangeOption(string Label, TimeSpan Span);

    private const int MaxLines = 3000;
    private const int MaxPages = 15;
    private static readonly RangeOption[] Ranges =
        [new("5 minutes", TimeSpan.FromMinutes(5)), new("15 minutes", TimeSpan.FromMinutes(15)), new("1 hour", TimeSpan.FromHours(1)),
         new("3 hours", TimeSpan.FromHours(3)), new("12 hours", TimeSpan.FromHours(12)), new("24 hours", TimeSpan.FromHours(24)),
         new("3 days", TimeSpan.FromDays(3))];

    private readonly AppSession _session;
    private readonly ObservableCollection<LogEvent> _lines = new();
    private readonly DispatcherTimer _live;
    private Target? _target;
    private EbEnvironmentStatus? _eb;
    private ResourceStatus? _resource;
    // What the shown lines are, for exports: source label, time range and filter of the last load.
    private string _shownSource = "";
    private DateTime? _shownFrom;
    private DateTime? _shownTo;
    private string? _shownFilter;
    private DateTime _lastEventUtc;
    // RDS log file being tailed (follows rotation to the next file of the same log).
    private LogSource? _rdsTailSource;
    private string? _rdsMarker;
    private int _rdsIdleTicks;
    private int _generation;
    private bool _busy;

    public LogsView(AppSession session)
    {
        InitializeComponent();
        _session = session;
        Lines.ItemsSource = _lines;
        Range.ItemsSource = Ranges;
        Range.SelectedIndex = 1;
        _live = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _live.Tick += async (_, _) => await TailAsync();
        CloudWatchBar.IsEnabled = false;
    }

    /// <summary>Called from the dashboard.</summary>
    public async void Open(Target target, ResourceStatus resource, LogFocus? focus = null)
    {
        var generation = ++_generation;
        Wipe();
        _target = target;
        _resource = resource;
        _eb = resource as EbEnvironmentStatus;
        Heading.Text = resource switch
        {
            EcsServiceStatus ecs => $"{ecs.Snapshot.ServiceName} — container logs",
            RdsInstanceStatus => $"{resource.DisplayName} — database logs",
            _ => $"{resource.DisplayName} — environment logs",
        };
        SubHeading.Text = $"{target.DisplayName} · {target.Region}" + resource switch
        {
            EcsServiceStatus e => $" · cluster {e.Snapshot.ClusterName}",
            RdsInstanceStatus db => $" · RDS {db.Snapshot.EngineText} · log files are read with DownloadDBLogFilePortion (read-only)",
            _ => " · Elastic Beanstalk",
        };
        EbBar.IsVisible = !(_eb is null);
        var hasElevated = !string.IsNullOrEmpty(target.ElevatedProfileName);
        TailButton.IsEnabled = BundleButton.IsEnabled = hasElevated;
        EbHint.Text = hasElevated
            ? $"Instance logs (collected by Elastic Beanstalk; uses the elevated key {target.ElevatedProfileName}, confirm each request)"
            : "Instance logs need an elevated profile for this target (Settings → Accounts & regions); the read-only key is never used for this.";
        Source.ItemsSource = null;
        CloudWatchBar.IsEnabled = false;
        SetStatus("Looking up log locations…");

        try
        {
            var sources = resource switch
            {
                EcsServiceStatus svc => await _session.Gateway.GetEcsLogSourcesAsync(target, svc.Snapshot, CancellationToken.None),
                EbEnvironmentStatus env => await _session.Gateway.GetEbLogSourcesAsync(target, env.Snapshot.EnvironmentName, CancellationToken.None),
                RdsInstanceStatus db => await _session.Gateway.GetRdsLogSourcesAsync(target, db.Snapshot, CancellationToken.None),
                _ => [],
            };
            if (generation != _generation)
                return;

            if (focus is not null)
                sources = FocusOnTask(sources, focus);
            Source.ItemsSource = sources;
            var usable = sources.FirstOrDefault(s => s.IsUsable);
            Source.SelectedItem = usable ?? sources.FirstOrDefault();
            CloudWatchBar.IsEnabled = usable is not null;
            if (usable is not null)
            {
                await LoadAsync();
            }
            else
            {
                SetStatus(resource switch
                {
                    EbEnvironmentStatus => "This environment does not stream logs to CloudWatch (enable \"Log streaming\" in the EB console), but you can request instance logs above.",
                    RdsInstanceStatus => "This database has no log files yet.",
                    _ => sources.Count == 0 ? "No containers found in the service's task definition." : sources[0].Note ?? "No CloudWatch log group configured.",
                });
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                SetStatus(Describe(ex));
        }
    }

    /// <summary>
    /// awslogs names streams &lt;prefix&gt;/&lt;container&gt;/&lt;task id&gt; (or just the task id without a prefix): put a source
    /// for exactly that stream first, and keep the container's other sources after it.
    /// </summary>
    private static IReadOnlyList<LogSource> FocusOnTask(IReadOnlyList<LogSource> sources, LogFocus focus)
    {
        var container = sources.FirstOrDefault(s => s.IsUsable && s.Label.StartsWith($"{focus.Container} ·", StringComparison.Ordinal));
        if (container is null)
            return sources;
        var task = container with
        {
            Label = $"{focus.Container} · task {focus.TaskId[..Math.Min(8, focus.TaskId.Length)]}… · {container.LogGroup}",
            StreamPrefix = container.StreamPrefix is null ? focus.TaskId : container.StreamPrefix + focus.TaskId,
        };
        return [task, .. sources];
    }

    /// <summary>On lock: log lines must not stay on screen.</summary>
    public void Wipe()
    {
        Live.IsChecked = false;
        _live.Stop();
        _lines.Clear();
        Bundles.ItemsSource = null;
        _rdsTailSource = null;
        _rdsMarker = null;
        SetStatus("Log lines are shown here only; Skypeek never stores them.");
    }

    private LogSource? SelectedSource => Source.SelectedItem as LogSource is { IsUsable: true } s ? s : null;

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A log file is read whole (its newest lines); the time range applies to CloudWatch sources only.
        var isFile = SelectedSource?.IsRdsFile == true;
        Range.IsEnabled = !isFile;
        Filter.PlaceholderText = isFile ? "Show only lines containing this text" : "CloudWatch filter pattern, e.g. ERROR or \"Timeout\"";
        if (IsLoaded && SelectedSource is not null && !_busy)
            _ = LoadAsync();
    }

    private void OnLoad(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = LoadAsync();
    }

    private void OnLiveChanged(object? sender, RoutedEventArgs e)
    {
        if (Live.IsChecked == true) _live.Start();
        else _live.Stop();
    }

    private async Task LoadAsync()
    {
        if (_target is not { } target || SelectedSource is not { } source || _busy)
            return;
        if (source.IsRdsFile)
        {
            await LoadRdsFileAsync(target, source);
            return;
        }
        var generation = ++_generation;
        _busy = true;
        LoadButton.IsEnabled = false;
        _lines.Clear();
        var end = DateTime.UtcNow;
        var start = end - ((Range.SelectedItem as RangeOption)?.Span ?? TimeSpan.FromMinutes(15));
        SetStatus($"Loading {source.LogGroup}…");
        try
        {
            // FilterLogEvents pages forward from the start; keep the newest lines.
            var collected = new List<LogEvent>();
            string? token = null;
            var pages = 0;
            do
            {
                var page = await _session.Gateway.GetLogEventsAsync(target, source, start, end, Filter.Text, token, CancellationToken.None);
                if (generation != _generation)
                    return;
                collected.AddRange(page.Events);
                if (collected.Count > MaxLines)
                    collected.RemoveRange(0, collected.Count - MaxLines);
                token = page.NextToken;
            } while (token is not null && ++pages < MaxPages);

            foreach (var line in collected)
                _lines.Add(line);
            _lastEventUtc = collected.Count > 0 ? collected[^1].Timestamp : end;
            (_shownSource, _shownFrom, _shownTo, _shownFilter) = ($"CloudWatch Logs {source.LogGroup}{(source.StreamPrefix is null ? "" : $" (streams {source.StreamPrefix}*)")}", start, end, Filter.Text);
            ScrollToEnd();
            var truncated = token is not null ? " (more exist — narrow the range or add a filter)" : "";
            SetStatus($"{collected.Count} line(s) from {start.ToLocalTime():g} to {end.ToLocalTime():t}{truncated}. Lines are never stored.");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                SetStatus(Describe(ex));
        }
        finally
        {
            if (generation == _generation)
            {
                _busy = false;
                LoadButton.IsEnabled = true;
            }
        }
    }

    private async Task TailAsync()
    {
        if (_target is not { } target || SelectedSource is not { } source || _busy || App.Current.IsLocked)
            return;
        if (source.IsRdsFile)
        {
            await TailRdsFileAsync(target);
            return;
        }
        _busy = true;
        try
        {
            var page = await _session.Gateway.GetLogEventsAsync(target, source, _lastEventUtc.AddMilliseconds(1), DateTime.UtcNow, Filter.Text, null, CancellationToken.None);
            foreach (var line in page.Events)
                _lines.Add(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            if (page.Events.Count > 0)
            {
                _lastEventUtc = page.Events[^1].Timestamp;
                ScrollToEnd();
            }
            SetStatus($"Live · {_lines.Count} line(s) · last check {DateTime.Now:T}");
        }
        catch (Exception ex)
        {
            SetStatus($"Live tail paused: {Describe(ex)}");
            Live.IsChecked = false;
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------------- RDS log files ----------------

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2}(?:\.\d+)?)")]
    private static partial Regex LineTimestamp();

    /// <summary>"error/postgresql.log.2026-09-28-10" and "error/mysql-error-running.log.3" → the log they rotate from.</summary>
    [GeneratedRegex(@"([.\-_]?\d{4}-\d{2}-\d{2}([.\-_]?\d{1,4})?|\.\d+)$")]
    private static partial Regex RotationSuffix();

    private static string LogFamily(string fileName) => RotationSuffix().Replace(fileName, "");

    /// <summary>RDS logs have no per-line metadata; take the time from the line (UTC) or carry the previous one.</summary>
    private static List<LogEvent> ParseRdsLines(string data, LogSource source, DateTime fallbackUtc)
    {
        var events = new List<LogEvent>();
        var current = fallbackUtc;
        foreach (var raw in data.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var match = LineTimestamp().Match(line);
            if (match.Success && DateTime.TryParse($"{match.Groups[1].Value} {match.Groups[2].Value}", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                current = ts;
            events.Add(new LogEvent(current, source.DbLogFile ?? "", line));
        }
        return events;
    }

    private bool MatchesFilter(LogEvent line) =>
        string.IsNullOrWhiteSpace(Filter.Text) || line.Message.Contains(Filter.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task LoadRdsFileAsync(Target target, LogSource source)
    {
        var generation = ++_generation;
        _busy = true;
        LoadButton.IsEnabled = false;
        _lines.Clear();
        SetStatus($"Reading {source.DbLogFile}…");
        try
        {
            // Without a marker RDS returns the newest lines of the file, plus a marker to continue from (live tail).
            var portion = await _session.Gateway.DownloadRdsLogAsync(target, source.DbInstance!, source.DbLogFile!, null, MaxLines, CancellationToken.None);
            if (generation != _generation)
                return;
            var lines = ParseRdsLines(portion.Data, source, source.LastWritten ?? DateTime.UtcNow).Where(MatchesFilter).TakeLast(MaxLines).ToList();
            foreach (var line in lines)
                _lines.Add(line);
            _rdsTailSource = source;
            _rdsMarker = portion.Marker;
            _rdsIdleTicks = 0;
            (_shownSource, _shownFrom, _shownTo, _shownFilter) = ($"RDS log file {source.DbLogFile} of {source.DbInstance} (last {MaxLines} lines)", null, null, Filter.Text);
            ScrollToEnd();
            SetStatus($"{lines.Count} line(s), the end of {source.DbLogFile}{(string.IsNullOrWhiteSpace(Filter.Text) ? "" : " matching the filter")}. Tick “Live tail” to follow it. Lines are never stored.");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                SetStatus(Describe(ex));
        }
        finally
        {
            if (generation == _generation)
            {
                _busy = false;
                LoadButton.IsEnabled = true;
            }
        }
    }

    private async Task TailRdsFileAsync(Target target)
    {
        if (_rdsTailSource is not { } source)
        {
            if (SelectedSource is { IsRdsFile: true } selected)
                await LoadRdsFileAsync(target, selected);
            return;
        }
        _busy = true;
        try
        {
            var added = 0;
            for (var round = 0; round < 5; round++)
            {
                var portion = await _session.Gateway.DownloadRdsLogAsync(target, source.DbInstance!, source.DbLogFile!, _rdsMarker, null, CancellationToken.None);
                _rdsMarker = portion.Marker ?? _rdsMarker;
                foreach (var line in ParseRdsLines(portion.Data, source, _lines.Count > 0 ? _lines[^1].Timestamp : DateTime.UtcNow).Where(MatchesFilter))
                {
                    _lines.Add(line);
                    added++;
                }
                if (!portion.AdditionalDataPending)
                    break;
            }
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            if (added > 0)
            {
                _rdsIdleTicks = 0;
                ScrollToEnd();
            }
            else if (++_rdsIdleTicks % 6 == 0 && await FindRotatedFileAsync(target, source) is { } next)
            {
                // The engine rotated to a new file (e.g. hourly for PostgreSQL); continue there from the start.
                _rdsTailSource = next;
                _rdsMarker = "0";
                _shownSource = $"RDS log file {next.DbLogFile} of {next.DbInstance} (live)";
                SetStatus($"Live · the log rotated; now following {next.DbLogFile}");
                return;
            }
            SetStatus($"Live · {source.DbLogFile} · {_lines.Count} line(s) · last check {DateTime.Now:T}");
        }
        catch (Exception ex)
        {
            SetStatus($"Live tail paused: {Describe(ex)}");
            Live.IsChecked = false;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<LogSource?> FindRotatedFileAsync(Target target, LogSource current)
    {
        if (_resource is not RdsInstanceStatus db)
            return null;
        var sources = await _session.Gateway.GetRdsLogSourcesAsync(target, db.Snapshot, CancellationToken.None);
        var family = LogFamily(current.DbLogFile!);
        return sources
            .Where(s => s.IsRdsFile && s.DbLogFile != current.DbLogFile && LogFamily(s.DbLogFile!) == family && s.LastWritten > current.LastWritten)
            .OrderByDescending(s => s.LastWritten)
            .FirstOrDefault();
    }

    // ---------------- Elastic Beanstalk instance logs ----------------

    private void OnRequestTail(object? sender, RoutedEventArgs e) => _ = RequestEbLogsAsync(bundle: false);

    private void OnRequestBundle(object? sender, RoutedEventArgs e) => _ = RequestEbLogsAsync(bundle: true);

    private async Task RequestEbLogsAsync(bool bundle)
    {
        if (_target is not { } target || _eb is not { } env)
            return;
        var generation = ++_generation;
        Live.IsChecked = false;
        TailButton.IsEnabled = BundleButton.IsEnabled = false;
        SetStatus("Waiting for your confirmation…");
        try
        {
            var progress = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (generation == _generation)
                    SetStatus($"Elastic Beanstalk is collecting logs from {env.Snapshot.InstanceIds.Count} instance(s)… this usually takes 10–60 seconds.");
            }), TaskScheduler.Default);
            var files = await _session.Gateway.RequestEbLogsAsync(target, env.Snapshot.EnvironmentId, env.Snapshot.EnvironmentName, bundle, CancellationToken.None);
            if (generation != _generation)
                return;
            if (files.Count == 0)
            {
                SetStatus("Elastic Beanstalk returned no logs in time. Try again in a minute.");
                return;
            }

            if (bundle)
            {
                Bundles.ItemsSource = files;
                SetStatus($"{files.Count} bundle(s) ready. The download links expire after about 15 minutes.");
                return;
            }

            _lines.Clear();
            foreach (var file in files)
            {
                var text = await _session.Gateway.DownloadEbLogAsync(target, file, CancellationToken.None);
                if (generation != _generation)
                    return;
                foreach (var line in text.Split('\n'))
                    _lines.Add(new LogEvent(file.SampledAt, file.InstanceId, line.TrimEnd('\r')));
            }
            ScrollToTop();
            (_shownSource, _shownFrom, _shownTo, _shownFilter) = ($"Elastic Beanstalk instance logs, last 100 lines of each file ({files.Count} instance(s))", null, null, null);
            SetStatus($"Last 100 lines of each log file from {files.Count} instance(s). Lines are never stored.");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                SetStatus(Describe(ex));
        }
        finally
        {
            TailButton.IsEnabled = BundleButton.IsEnabled = true;
        }
    }

    private void OnOpenBundle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: EbLogFile file }
            && Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
            Shell.OpenUrl(uri.AbsoluteUri);
    }

    // ---------------- helpers ----------------

    private void OnLinesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && Lines.SelectedItems is { Count: > 0 })
        {
            var selected = Lines.SelectedItems!.Cast<LogEvent>().OrderBy(l => _lines.IndexOf(l));
            SecureClipboard.CopyPlain(Join(selected));
            e.Handled = true;
        }
    }

    // ---------------- export for AI analysis ----------------

    private LogExportContext? ExportContext()
    {
        if (_target is null || _lines.Count == 0)
        {
            SetStatus("Load some log lines first.");
            return null;
        }
        // Use the freshest health snapshot of the resource so the export carries the current causes and events.
        var fresh = _resource is null ? null : _session.Health.Get(_target.Id) is { } h
            ? h.AllResources.FirstOrDefault(r => r.ResourceKey == _resource.ResourceKey) ?? _resource
            : _resource;
        return new LogExportContext(_target, _session.Catalog.AccountOf(_target.ProfileName), fresh, _shownSource, _shownFrom, _shownTo, _shownFilter);
    }

    private void OnCopyForAi(object? sender, RoutedEventArgs e)
    {
        if (ExportContext() is not { } ctx)
            return;
        SecureClipboard.CopyPlain(LogExport.ToMarkdown(ctx, _lines.ToList(), DateTime.UtcNow));
        SetStatus($"Copied {_lines.Count} line(s) with context — paste into your AI assistant. Log lines can contain sensitive data.");
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (ExportContext() is not { } ctx || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export logs for AI analysis",
            SuggestedFileName = LogExport.SuggestFileName(ctx, DateTime.UtcNow, "md"),
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Markdown with context") { Patterns = ["*.md"] },
                new Avalonia.Platform.Storage.FilePickerFileType("JSON Lines") { Patterns = ["*.jsonl"] },
                new Avalonia.Platform.Storage.FilePickerFileType("Plain text") { Patterns = ["*.txt"] },
            ],
        });
        if (file is null)
            return;

        var lines = _lines.ToList();
        var content = file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? LogExport.ToJsonLines(ctx, lines, DateTime.UtcNow)
            : LogExport.ToMarkdown(ctx, lines, DateTime.UtcNow);
        try
        {
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                await writer.WriteAsync(content);
            var shown = file.Path.IsFile ? file.Path.LocalPath : file.Name;
            SetStatus($"Exported {lines.Count} line(s) to {shown}. This file is not encrypted — delete it when you are done.");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
    }

    private void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (_lines.Count > 0)
            SecureClipboard.CopyPlain(Join(_lines));
    }

    private static string Join(IEnumerable<LogEvent> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(l.TimeText).Append("  ").Append(l.Stream).Append("  ").AppendLine(l.Message);
        return sb.ToString();
    }

    private void ScrollToEnd()
    {
        if (_lines.Count > 0)
            Lines.ScrollIntoView(_lines[^1]);
    }

    private void ScrollToTop()
    {
        if (_lines.Count > 0)
            Lines.ScrollIntoView(_lines[0]);
    }

    private void SetStatus(string text) => Status.Text = text;

    private string Describe(Exception ex) => ex switch
    {
        ElevationDeniedException => "Not approved; nothing was sent to AWS.",
        AmazonServiceException a when AwsErrorClassifier.IsAccessDenied(a.ErrorCode) =>
            $"The role is not allowed to do this ({a.ErrorCode}).",
        AmazonServiceException a => $"{a.ErrorCode}: {a.Message}",
        _ => ex.Message,
    };
}
