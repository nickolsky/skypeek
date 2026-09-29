using System.Windows;
using System.Windows.Controls;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>
/// Chart period switch in a resource's details: CPU/memory per node over 1 hour to 7 days, read from CloudWatch on
/// demand. The chosen period sticks, so the next resource opens with it; results are kept for a few minutes.
/// </summary>
public partial class HistoryPanel
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<(string Key, int Hours), (DateTime At, IReadOnlyList<HistorySeries> Series)> Cache = new();
    /// <summary>Period chosen last (hours); null until the user picks one, so nothing is read unasked.</summary>
    private static int? _hours;
    private static int _instances;

    public static readonly DependencyProperty ResourceProperty = DependencyProperty.Register(
        nameof(Resource), typeof(ResourceStatus), typeof(HistoryPanel), new PropertyMetadata(null, (d, _) => ((HistoryPanel)d).OnResourceChanged()));

    public ResourceStatus? Resource
    {
        get => (ResourceStatus?)GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    /// <summary>Each panel has its own radio group.</summary>
    public string GroupName { get; } = $"history{Interlocked.Increment(ref _instances)}";

    private readonly string _intro;
    private bool _syncing;

    public HistoryPanel()
    {
        InitializeComponent();
        _intro = StatusText.Text;
    }

    /// <summary>Called on lock.</summary>
    public static void ClearCache() => Cache.Clear();

    private void OnResourceChanged()
    {
        // WPF reuses the panel for another resource of the same type: show only that resource's data.
        SeriesList.ItemsSource = null;
        _syncing = true;
        foreach (var button in Periods.Children.OfType<RadioButton>())
            button.IsChecked = _hours is { } h && button.Tag is string tag && tag == h.ToString();
        _syncing = false;
        if (_hours is { } hours && Resource is { } r)
            _ = LoadAsync(r, hours);
        else
            StatusText.Text = _intro;
    }

    private void OnPeriod(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var hours) || Resource is not { } r)
            return;
        _hours = hours;
        _ = LoadAsync(r, hours);
    }

    private async Task LoadAsync(ResourceStatus resource, int hours)
    {
        if (App.Current.Session is not { } session || session.Settings.FindTarget(resource.TargetId) is not { } target)
            return;
        if (Cache.TryGetValue((resource.ResourceKey, hours), out var cached) && DateTime.UtcNow - cached.At < CacheFor)
        {
            Show(cached.Series, hours);
            return;
        }
        StatusText.Text = $"Reading the last {Label(hours)} from CloudWatch…";
        try
        {
            var series = await session.Health.GetHistoryAsync(target, resource, TimeSpan.FromHours(hours), CancellationToken.None);
            Cache[(resource.ResourceKey, hours)] = (DateTime.UtcNow, series);
            if (Resource?.ResourceKey == resource.ResourceKey && _hours == hours)
                Show(series, hours);
        }
        catch (Exception ex)
        {
            if (Resource?.ResourceKey == resource.ResourceKey)
                StatusText.Text = $"Could not read history: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
    }

    private void Show(IReadOnlyList<HistorySeries> series, int hours)
    {
        SeriesList.ItemsSource = series;
        var points = series.SelectMany(s => s.Evaluation.Points).ToList();
        StatusText.Text = series.Count == 0 ? "No CPU/memory metrics for this resource."
            : points.Count == 0 ? $"No data in the last {Label(hours)}."
            : $"Last {Label(hours)}: {points.Min(p => p.Timestamp).ToLocalTime():g} – {points.Max(p => p.Timestamp).ToLocalTime():g}, "
              + $"one point per {Math.Max(1, series.Max(s => s.Evaluation.PeriodSeconds) / 60)} min (average). Hover a chart for details.";
    }

    private static string Label(int hours) => hours < 48 ? $"{hours} h" : $"{hours / 24} days";
}
