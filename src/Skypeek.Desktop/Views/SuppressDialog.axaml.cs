using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

public partial class SuppressDialog : DialogWindow
{
    private readonly AppSession _session;
    private readonly Target _target;

    public SuppressDialog(AppSession session, Target target, string alarmName)
    {
        InitializeComponent();
        _session = session;
        _target = target;
        ThisTarget.Content = $"Only {target.DisplayName} ({target.Region})";
        Pattern.Text = alarmName;
        Opened += (_, _) =>
        {
            Pattern.Focus();
            Pattern.SelectAll();
        };
    }

    public AlarmSuppression? Result { get; private set; }

    private long? Scope => AllTargets.IsChecked == true ? null : _target.Id;

    private void OnPatternChanged(object? sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent (IsChecked in XAML) before the fields are set.
        if (_session is null || Preview is null)
            return;
        var pattern = Pattern.Text?.Trim() ?? "";
        if (pattern.Length == 0)
        {
            Preview.Text = "";
            return;
        }
        var targetIds = Scope is { } id ? new HashSet<long> { id } : _session.Settings.Targets.Select(t => t.Id).ToHashSet();
        var matches = _session.Health.Snapshot()
            .Where(h => targetIds.Contains(h.TargetId))
            .SelectMany(HealthRules.AllAlarms)
            .Where(a => a.IsActive && HealthRules.WildcardMatch(pattern, a.Name))
            .Select(a => a.Name)
            .Distinct()
            .ToList();
        Preview.Text = matches.Count == 0
            ? "Matches no alarm that is currently in ALARM (it will still apply to future ones)."
            : $"Matches {matches.Count} alarm(s) currently in ALARM: {string.Join(", ", matches.Take(4))}{(matches.Count > 4 ? ", …" : "")}";
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var pattern = Pattern.Text?.Trim() ?? "";
        if (pattern.Length == 0 || pattern.Replace("*", "").Replace("?", "").Length == 0)
        {
            Error.Text = "Enter an alarm name or a pattern with some fixed text (a bare * would hide every alarm).";
            Error.IsVisible = true;
            return;
        }
        Result = new AlarmSuppression(pattern, Scope, DateTime.UtcNow);
        Finish(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);
}
