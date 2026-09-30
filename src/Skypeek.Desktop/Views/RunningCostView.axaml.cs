using Avalonia.Controls;
using Avalonia.Interactivity;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>Settings → Cost of running Skypeek: counted paid calls this month, projection and the settings-based estimate.</summary>
public partial class RunningCostView : UserControl
{
    private readonly AppSession? _session;

    public RunningCostView() => InitializeComponent();

    public RunningCostView(AppSession session) : this() => _session = session;

    public void Refresh()
    {
        if (_session is not { } session)
            return;
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<ApiUsageRow> usage = [];
        DateTime? first = null;
        try
        {
            usage = session.Repository.Usage(PaidApi.MonthOf(monthStart.AddMonths(-12)));
            first = session.Repository.FirstLogged(monthStart);
        }
        catch
        {
            // Nothing counted is shown as nothing counted.
        }

        var catalog = session.Repository.LoadAll();
        var estimates = session.Settings.Targets.Select(t => RunningCost.Estimate(t,
            session.Health.MetricCount(t.Id),
            catalog.Count(i => i.TargetId == t.Id && i.Kind == CatalogKind.Secret),
            session.Health.Get(t.Id) is { } h ? h.AllResources.Sum(r => r.Alarms.Count) + h.OtherAlarms.Count : 0)).ToList();
        var report = RunningCostReport.Build(usage, estimates,
            profile => profile is not null && session.Monitor.Profiles.TryGetValue(profile, out var p) ? p.AccountId : null, now, first);

        MonthToDate.Text = $"${report.MonthToDateUsd:0.00}";
        Projected.Text = $"${report.ProjectedUsd:0.00}";
        Estimated.Text = $"${report.EstimatedMonthlyUsd:0.00}";
        CountingSince.Text = report.CountingSinceUtc > monthStart
            ? $"counted since {report.CountingSinceUtc.ToLocalTime():g} (when counting started)"
            : $"since {monthStart.ToLocalTime():MMMM d}";
        MeterList.ItemsSource = report.ThisMonth;
        NoCalls.IsVisible = report.ThisMonth.Count == 0;
        TargetList.ItemsSource = report.Targets;
        MonthList.ItemsSource = report.PreviousMonths;
        HistoryPanel.IsVisible = report.PreviousMonths.Count > 0;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Refresh();
}
