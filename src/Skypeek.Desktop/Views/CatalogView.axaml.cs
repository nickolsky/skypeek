using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

/// <summary>Secrets &amp; parameters tab: search on the left, details of the selected item on the right.</summary>
public partial class CatalogView : UserControl
{
    private const string AnyRegion = "All regions";

    public sealed record TargetOption(string Label, long? TargetId);

    private readonly AppSession _session;
    private readonly DispatcherTimer _debounce;
    private readonly DetailsView _details;

    public CatalogView(AppSession session)
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _session = session;
        _details = new DetailsView(session);
        DetailsHost.Content = _details;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RunSearch();
        };

        session.Catalog.CatalogChanged += OnCatalogChanged;
        RefreshFilters();
        RunSearch();
    }

    public void Detach() => _session.Catalog.CatalogChanged -= OnCatalogChanged;

    public void FocusSearch()
    {
        Query.Focus();
        Query.SelectAll();
    }

    /// <summary>Called on lock: revealed values must not survive.</summary>
    public void Wipe() => _details.Wipe();

    private void OnCatalogChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshFilters();
        RunSearch();
    });

    private void RefreshFilters()
    {
        var targets = _session.Settings.Targets;
        List<TargetOption> targetOptions = [new("All targets", null), .. targets.Select(t => new TargetOption(t.DisplayName, t.Id))];
        var selectedTarget = (TargetFilter.SelectedItem as TargetOption)?.TargetId;
        TargetFilter.ItemsSource = targetOptions;
        TargetFilter.SelectedItem = targetOptions.FirstOrDefault(o => o.TargetId == selectedTarget) ?? targetOptions[0];

        List<string> regions = [AnyRegion, .. targets.Select(t => t.Region).Distinct().OrderBy(r => r)];
        var selectedRegion = RegionFilter.SelectedItem as string;
        RegionFilter.ItemsSource = regions;
        RegionFilter.SelectedItem = selectedRegion is not null && regions.Contains(selectedRegion) ? selectedRegion : regions[0];
    }

    private void OnQueryChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RunSearch();
    }

    private void RunSearch()
    {
        var kind = KindSecrets.IsChecked == true ? CatalogKind.Secret : KindParams.IsChecked == true ? CatalogKind.Parameter : (CatalogKind?)null;
        var targetId = (TargetFilter.SelectedItem as TargetOption)?.TargetId;
        var region = RegionFilter.SelectedItem as string is { } r && r != AnyRegion ? r : null;

        var selectedKey = Results.SelectedItem is SearchResult sel ? (sel.Item.TargetId, sel.Item.Kind, sel.Item.Name) : default;
        var results = _session.Catalog.Index.Search(Query.Text ?? "", new SearchFilter(kind, Region: region, TargetId: targetId));
        Results.ItemsSource = results;
        var keep = results.FirstOrDefault(x => (x.Item.TargetId, x.Item.Kind, x.Item.Name) == selectedKey);
        if (keep is not null)
            Results.SelectedItem = keep;

        var total = _session.Catalog.Index.Count;
        EmptyHint.IsVisible = results.Count == 0;
        EmptyHint.Text = _session.Settings.Targets.Count == 0
            ? "No targets yet. Open the Settings tab to choose accounts and regions."
            : total == 0
                ? "The catalog is empty. Lists download on the configured schedule — use Dashboard → Refresh catalogs to fetch now."
                : "No matches.";
        StatusText.Text = $"{results.Count} of {total} items · ↑↓ select · Ctrl+C copy name · Ctrl+F search · Esc hide";
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e) =>
        _details.Show(Results.SelectedItem as SearchResult);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Query.IsKeyboardFocusWithin)
            return;
        switch (e.Key)
        {
            case Key.Down when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Min(Results.Items.Count - 1, Results.SelectedIndex + 1);
                if (Results.SelectedItem is { } down)
                    Results.ScrollIntoView(down);
                e.Handled = true;
                break;
            case Key.Up when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
                if (Results.SelectedItem is { } up)
                    Results.ScrollIntoView(up);
                e.Handled = true;
                break;
            case Key.Enter when Results.Items.Count > 0:
                if (Results.SelectedIndex < 0)
                    Results.SelectedIndex = 0;
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control && Query.SelectionStart == Query.SelectionEnd && Results.SelectedItem is SearchResult sel:
                SecureClipboard.CopyPlain(sel.Item.Name);
                StatusText.Text = $"Copied name: {sel.Item.Name}";
                e.Handled = true;
                break;
        }
    }
}
