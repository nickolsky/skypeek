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

    /// <summary>What the listed secrets and Advanced parameters cost a month (recomputed when the lists change).</summary>
    private double _catalogMonthly;

    private void RefreshFilters()
    {
        _catalogMonthly = _session.Settings.Targets.Select(t => _session.Costs.CatalogEstimate(t)?.MonthlyUsd ?? 0).Sum();
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
        var monthly = _catalogMonthly;
        StatusText.Text = $"{results.Count} of {total} items{(monthly > 0 ? $" · secrets and Advanced parameters ≈ {CostRules.Money(monthly)}/month" : "")} · ↑↓ select · Ctrl+C copy name · Ctrl+F search · Esc hide";
    }

    private void OnNewSecret(object? sender, RoutedEventArgs e) => _ = CreateAsync(CatalogKind.Secret);

    private void OnNewParameter(object? sender, RoutedEventArgs e) => _ = CreateAsync(CatalogKind.Parameter);

    private async Task CreateAsync(CatalogKind kind)
    {
        var targets = _session.Settings.Targets
            .Where(t => t.Enabled && !string.IsNullOrEmpty(t.ElevatedProfileName) && (kind == CatalogKind.Secret ? t.SecretsEnabled : t.ParamsEnabled))
            .ToList();
        if (targets.Count == 0)
        {
            await ConfirmDialog.InformAsync(Dialogs.OwnerOf(this), "No target can create it",
                "Creating uses a target's elevated key. Set one in Settings → Accounts & regions.");
            return;
        }
        var preselected = (TargetFilter.SelectedItem as TargetOption)?.TargetId is { } id ? targets.FirstOrDefault(t => t.Id == id) : null;
        var dialog = new SecretEditorDialog(kind, null, null, targets, null, preselected);
        if (!await Dialogs.ShowAsync(dialog, Dialogs.OwnerOf(this)) || dialog.Result is not { } edit)
            return;
        try
        {
            if (kind == CatalogKind.Secret)
                await _session.Gateway.CreateSecretAsync(edit.Target, edit.Name, edit.Value, edit.Description, edit.KmsKeyId, CancellationToken.None);
            else
                await _session.Gateway.CreateParameterAsync(edit.Target, edit.Name, edit.Value, edit.Type ?? "SecureString", edit.Description, edit.Tier, edit.KmsKeyId, CancellationToken.None);
            var created = kind == CatalogKind.Secret
                ? await _session.Gateway.DescribeSecretAsync(edit.Target, edit.Name, CancellationToken.None)
                : await _session.Gateway.DescribeParameterAsync(edit.Target, edit.Name, CancellationToken.None);
            if (created is not null)
            {
                created.FirstSeen = DateTime.UtcNow;
                _session.Catalog.Upsert(created);
            }
            Query.Text = edit.Name;
            StatusText.Text = $"Created {edit.Name} in {edit.Target.DisplayName}.";
        }
        catch (Core.ElevationDeniedException)
        {
            StatusText.Text = "Not approved; nothing was sent to AWS.";
        }
        catch (Exception ex)
        {
            await ConfirmDialog.InformAsync(Dialogs.OwnerOf(this), $"{edit.Name} was not created",
                ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message);
            App.Current.AskToSignInIfNeeded(ex);
        }
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
