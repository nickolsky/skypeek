using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using System.Text.Json;
using Amazon.Runtime;
using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

/// <summary>Details of one secret/parameter with on-demand value reveal (read-only key, or elevated key after approval).</summary>
public partial class DetailsView : UserControl
{
    private static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(30);

    private readonly AppSession _session;
    private readonly DispatcherTimer _hideTimer;
    private SearchResult? _result;
    private CatalogItem? _item;
    private string? _value;
    private DateTime _hideAt;
    private int _generation;

    public DetailsView(AppSession session)
    {
        InitializeComponent();
        _session = session;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) => UpdateCountdown();
    }

    private Target? CurrentTarget => _result is null ? null : _session.Settings.FindTarget(_result.Target.Id) ?? _result.Target;

    public void Show(SearchResult? result)
    {
        Wipe();
        _generation++;
        _result = result;
        _item = result?.Item;
        Placeholder.IsVisible = result is null;
        DetailsBody.IsVisible = !(result is null);
        if (result is null || _item is null)
            return;

        var target = CurrentTarget!;
        ValueError.IsVisible = false;
        NameText.Text = _item.Name;
        KindText.Text = result.KindLabel;
        LocationText.Text = $"{target.DisplayName} · {target.Region} · {result.AccountId ?? target.ProfileName}";
        CopyArnButton.IsVisible = !(_item.Arn is null);
        RefreshMetaButton.IsVisible = _item.Kind == CatalogKind.Secret;
        RevealButton.Content = _item.Kind == CatalogKind.Parameter && !_item.IsSecureParameter ? "Show value" : "Reveal";
        RevealElevatedButton.IsVisible = !(string.IsNullOrEmpty(target.ElevatedProfileName));
        ToolTip.SetTip(RevealElevatedButton, target.ElevatedProfileName is { } p ? $"Uses {p}; you will be asked to approve the call." : null);
        ValueHint.Text = (_item.Kind == CatalogKind.Secret
            ? "Calls secretsmanager:GetSecretValue with the read-only key."
            : _item.IsSecureParameter
                ? "SecureString: calls ssm:GetParameter with decryption using the read-only key."
                : "Calls ssm:GetParameter with the read-only key.") + " Values are never stored and hide after 30 seconds.";
        RenderMetadata();
        var canChange = !string.IsNullOrEmpty(target.ElevatedProfileName);
        EditButton.IsEnabled = DeleteButton.IsEnabled = canChange;
        RestoreButton.IsVisible = false;
        ChangeStatus.IsVisible = false;
        ChangeHint.Text = !canChange
            ? "Set an elevated key for this target (Settings → Accounts & regions) to change or delete it."
            : _item.Kind == CatalogKind.Secret
                ? "Uses the elevated key and asks each time. Deleting schedules it for deletion (7–30 days, can be cancelled) and asks you to type the name."
                : "Uses the elevated key and asks each time. Deleting is immediate and permanent and asks you to type the name.";
    }

    // ---------------- change ----------------

    private void ChangeResult(string text, bool ok)
    {
        ChangeStatus.Text = text;
        ChangeStatus.Foreground = ok ? LevelToBrushConverter.Ok : LevelToBrushConverter.Critical;
        ChangeStatus.IsVisible = true;
    }

    /// <summary>Fresh metadata (version, last change) so the edit can tell when someone else changed it meanwhile.</summary>
    private async Task<CatalogItem> FreshAsync(Target target, CatalogItem item)
    {
        try
        {
            var fresh = item.Kind == CatalogKind.Secret
                ? await _session.Gateway.DescribeSecretAsync(target, item.Arn ?? item.Name, CancellationToken.None)
                : await _session.Gateway.DescribeParameterAsync(target, item.Name, CancellationToken.None);
            return fresh ?? item;
        }
        catch (Exception ex) when (ex is not ElevationDeniedException)
        {
            return item;
        }
    }

    private async void OnEditValue(object? sender, RoutedEventArgs e)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        var item = await FreshAsync(target, _item);
        var dialog = new SecretEditorDialog(item.Kind, item, _value, [], async () =>
        {
            try
            {
                var current = item.Kind == CatalogKind.Secret
                    ? await _session.Gateway.GetSecretValueAsync(target, item.Arn ?? item.Name, elevated: true, CancellationToken.None)
                    : await _session.Gateway.GetParameterValueAsync(target, item.Name, item.IsSecureParameter, elevated: true, CancellationToken.None);
                return current.Value;
            }
            catch (Exception ex)
            {
                ChangeResult($"Could not read the current value: {Describe(ex)}", false);
                return null;
            }
        });
        if (!await Dialogs.ShowAsync(dialog, Dialogs.OwnerOf(this)) || dialog.Result is not { } edit)
            return;
        try
        {
            if (item.Kind == CatalogKind.Secret)
                await _session.Gateway.UpdateSecretValueAsync(target, item, edit.Value, CancellationToken.None);
            else
                await _session.Gateway.PutParameterValueAsync(target, item, edit.Value, CancellationToken.None);
            Wipe();
            var saved = await FreshAsync(target, item);
            saved.FirstSeen = item.FirstSeen;
            _session.Catalog.Upsert(saved);
            ChangeResult($"Saved at {DateTime.Now:T}{(saved.Version is { } v ? $" (version {v})" : "")}.", true);
        }
        catch (Exception ex)
        {
            ChangeResult(ex is ElevationDeniedException ? "Not approved; nothing was sent to AWS." : $"Not saved: {Describe(ex)}", false);
            App.Current.AskToSignInIfNeeded(ex);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        var item = _item;
        var days = 30;
        if (item.Kind == CatalogKind.Secret)
        {
            // The longest recovery window: a mistaken delete can be undone for a month.
            var keep = await ConfirmDialog.AskAsync(Dialogs.OwnerOf(this), $"Delete {item.Name}?",
                $"The secret is scheduled for deletion in {days} days and can be restored until then (Cancel deletion). "
                + "Reading it fails right away. Next, the permission dialog asks you to type the name.",
                "Continue…", danger: true);
            if (!keep)
                return;
        }
        try
        {
            if (item.Kind == CatalogKind.Secret)
                await _session.Gateway.DeleteSecretAsync(target, item, days, CancellationToken.None);
            else
                await _session.Gateway.DeleteParameterAsync(target, item, CancellationToken.None);
            Wipe();
            _session.Catalog.Remove(item.TargetId, item.Kind, item.Name);
            if (item.Kind == CatalogKind.Secret)
            {
                ChangeResult($"Scheduled for deletion on {DateTime.Now.AddDays(days):D}. Cancel deletion brings it back.", true);
                RestoreButton.IsVisible = true;
                EditButton.IsEnabled = DeleteButton.IsEnabled = false;
            }
            else
            {
                ChangeResult("Deleted.", true);
                EditButton.IsEnabled = DeleteButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            ChangeResult(ex is ElevationDeniedException ? "Not approved; nothing was sent to AWS." : $"Not deleted: {Describe(ex)}", false);
            App.Current.AskToSignInIfNeeded(ex);
        }
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        try
        {
            await _session.Gateway.RestoreSecretAsync(target, _item, CancellationToken.None);
            var restored = await FreshAsync(target, _item);
            _session.Catalog.Upsert(restored);
            RestoreButton.IsVisible = false;
            EditButton.IsEnabled = DeleteButton.IsEnabled = true;
            ChangeResult("Deletion cancelled; the secret is back.", true);
        }
        catch (Exception ex)
        {
            ChangeResult(ex is ElevationDeniedException ? "Not approved; nothing was sent to AWS." : $"Could not cancel the deletion: {Describe(ex)}", false);
        }
    }

    private static string Describe(Exception ex) => ex is AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message;

    /// <summary>List price per month: the region's price when cost estimates are on for the target (else the usual one).</summary>
    private string PriceText(CatalogItem item)
    {
        if (CurrentTarget is not { } target)
            return "";
        if (item.Kind == CatalogKind.Parameter && item.Tier is not ("Advanced" or "Intelligent-Tiering"))
            return "free (Standard tier)";
        var key = item.Kind == CatalogKind.Secret ? CostRules.SecretKey : CostRules.AdvancedParameterKey;
        var monthly = _session.Costs.UnitPrice(target, key) is { } usd ? item.Kind == CatalogKind.Secret ? usd : usd * CostRules.HoursPerMonth : (double?)null;
        var usual = item.Kind == CatalogKind.Secret ? 0.40 : 0.05;
        return monthly is { } m
            ? $"{CostRules.Money(m)} a month (list price in {target.Region}; plus API calls)"
            : $"about {CostRules.Money(usual)} a month (usual list price; turn on cost estimates for the target for its region's price)";
    }

    private void RenderMetadata()
    {
        if (_item is null)
            return;
        var rows = new List<KeyValuePair<string, string>>();
        void Add(string key, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
                rows.Add(new(key, text));
        }

        Add("ARN", _item.Arn);
        Add("Description", _item.Description);
        Add("Type", _item.Type);
        Add("Tier", _item.Tier);
        Add("Data type", _item.DataType);
        Add("Version", _item.Version);
        Add("KMS key", _item.KmsKeyId);
        Add("Rotation", _item.RotationEnabled is null ? null : _item.RotationEnabled.Value ? "enabled" : "disabled");
        Add("Price", PriceText(_item));
        Add("Last modified", _item.LastModified?.ToLocalTime().ToString("f"));
        Add("Modified by", _item.LastModifiedBy);
        Add("Last accessed", _item.LastAccessed?.ToLocalTime().ToString("D"));
        Add("Created", _item.Created?.ToLocalTime().ToString("f"));
        foreach (var (k, v) in _item.Tags.OrderBy(t => t.Key))
            Add($"Tag: {k}", v);
        Add("First seen", _item.FirstSeen == DateTime.MinValue ? "initial sync" : _item.FirstSeen.ToLocalTime().ToString("f"));
        Add("List fetched", _item.FetchedAt.ToLocalTime().ToString("f"));
        Metadata.ItemsSource = rows;
    }

    private void OnReveal(object? sender, RoutedEventArgs e) => _ = RevealAsync(elevated: false);

    private void OnRevealElevated(object? sender, RoutedEventArgs e) => _ = RevealAsync(elevated: true);

    private async Task RevealAsync(bool elevated)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        var generation = _generation;
        var item = _item;
        SetBusy(true);
        ValueError.IsVisible = false;
        try
        {
            var result = item.Kind == CatalogKind.Secret
                ? await _session.Gateway.GetSecretValueAsync(target, item.Arn ?? item.Name, elevated, CancellationToken.None)
                : await _session.Gateway.GetParameterValueAsync(target, item.Name, item.IsSecureParameter, elevated, CancellationToken.None);
            if (App.Current.IsLocked || generation != _generation)
                return;
            ShowValue(result.Value, elevated);
        }
        catch (AmazonServiceException ex) when (AwsErrorClassifier.IsAccessDenied(ex.ErrorCode))
        {
            var hint = !elevated && !string.IsNullOrEmpty(target.ElevatedProfileName)
                ? " Try \"Reveal with elevated key\"."
                : !elevated ? " Configure an elevated profile for this target in Settings to read it." : "";
            ShowError($"{(elevated ? "The elevated" : "The read-only")} role cannot read this value ({ex.ErrorCode}).{hint}");
        }
        catch (ElevationDeniedException)
        {
            ShowError("Elevated access was not approved; nothing was sent to AWS.");
        }
        catch (AmazonServiceException ex)
        {
            ShowError($"{ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is CredentialsUnavailableException or InvalidOperationException)
        {
            ShowError(ex.Message);
            App.Current.AskToSignInIfNeeded(ex);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (generation == _generation)
                SetBusy(false);
        }
    }

    private void ShowValue(string value, bool elevated)
    {
        _value = value;
        MaskedValue.IsVisible = false;
        HideButton.IsVisible = true;
        CopyValueButton.IsEnabled = true;

        var pairs = TryParseJsonObject(value);
        if (pairs is not null)
        {
            JsonGrid.ItemsSource = pairs;
            JsonGrid.IsVisible = true;
            ValueText.IsVisible = false;
        }
        else
        {
            ValueText.Text = value;
            ValueText.IsVisible = true;
            JsonGrid.IsVisible = false;
        }

        if (IsSensitive)
        {
            _hideAt = DateTime.Now + AutoHideAfter;
            _hideTimer.Start();
            UpdateCountdown();
        }
        if (elevated)
            Countdown.Text = "(elevated key) " + Countdown.Text;
    }

    private bool IsSensitive => _item is not null && (_item.Kind == CatalogKind.Secret || _item.IsSecureParameter);

    private static List<KeyValuePair<string, string>>? TryParseJsonObject(string value)
    {
        if (!value.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void UpdateCountdown()
    {
        var left = _hideAt - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            Wipe();
            return;
        }
        Countdown.Text = $"hides in {left.TotalSeconds:0}s";
    }

    private void OnHide(object? sender, RoutedEventArgs e) => Wipe();

    public void Wipe()
    {
        _hideTimer.Stop();
        _value = null;
        ValueText.Text = "";
        JsonGrid.ItemsSource = null;
        ValueText.IsVisible = false;
        JsonGrid.IsVisible = false;
        MaskedValue.IsVisible = true;
        HideButton.IsVisible = false;
        CopyValueButton.IsEnabled = false;
        Countdown.Text = "";
        SetBusy(false);
    }

    private void OnCopyValue(object? sender, RoutedEventArgs e)
    {
        if (_value is not null)
            CopyValue(_value);
    }

    private void OnCopyJsonValue(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string value })
            CopyValue(value);
    }

    private void CopyValue(string value)
    {
        if (IsSensitive)
        {
            _ = SecureClipboard.CopySecretAsync(value);
            Countdown.Text = $"copied — clipboard clears in {SecureClipboard.ClearAfter.TotalSeconds:0}s";
        }
        else
        {
            SecureClipboard.CopyPlain(value);
        }
    }

    private void OnCopyName(object? sender, RoutedEventArgs e)
    {
        if (_item is not null)
            SecureClipboard.CopyPlain(_item.Name);
    }

    private void OnCopyArn(object? sender, RoutedEventArgs e)
    {
        if (_item?.Arn is not null)
            SecureClipboard.CopyPlain(_item.Arn);
    }

    private async void OnRefreshMetadata(object? sender, RoutedEventArgs e)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        try
        {
            RefreshMetaButton.IsEnabled = false;
            if (await _session.Gateway.DescribeSecretAsync(target, _item.Arn ?? _item.Name, CancellationToken.None) is { } fresh)
            {
                fresh.FirstSeen = _item.FirstSeen;
                fresh.FetchedAt = DateTime.UtcNow;
                _item = fresh;
                RenderMetadata();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            RefreshMetaButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ValueError.Text = message;
        ValueError.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        ValueBusy.IsVisible = busy;
        RevealButton.IsEnabled = !busy;
        RevealElevatedButton.IsEnabled = !busy;
    }
}
