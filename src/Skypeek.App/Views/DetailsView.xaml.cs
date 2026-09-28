using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Amazon.Runtime;
using Skypeek.App.Infrastructure;
using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.App.Views;

/// <summary>Details of one secret/parameter with on-demand value reveal (read-only key, or elevated key after approval).</summary>
public partial class DetailsView
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
        Placeholder.Visibility = result is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsBody.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        if (result is null || _item is null)
            return;

        var target = CurrentTarget!;
        ValueError.Visibility = Visibility.Collapsed;
        NameText.Text = _item.Name;
        KindText.Text = result.KindLabel;
        LocationText.Text = $"{target.DisplayName} · {target.Region} · {result.AccountId ?? target.ProfileName}";
        CopyArnButton.Visibility = _item.Arn is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshMetaButton.Visibility = _item.Kind == CatalogKind.Secret ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.Content = _item.Kind == CatalogKind.Parameter && !_item.IsSecureParameter ? "Show value" : "Reveal";
        RevealElevatedButton.Visibility = string.IsNullOrEmpty(target.ElevatedProfileName) ? Visibility.Collapsed : Visibility.Visible;
        RevealElevatedButton.ToolTip = target.ElevatedProfileName is { } p ? $"Uses {p}; you will be asked to approve the call." : null;
        ValueHint.Text = (_item.Kind == CatalogKind.Secret
            ? "Calls secretsmanager:GetSecretValue with the read-only key."
            : _item.IsSecureParameter
                ? "SecureString: calls ssm:GetParameter with decryption using the read-only key."
                : "Calls ssm:GetParameter with the read-only key.") + " Values are never stored and hide after 30 seconds.";
        RenderMetadata();
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

    private void OnReveal(object sender, RoutedEventArgs e) => _ = RevealAsync(elevated: false);

    private void OnRevealElevated(object sender, RoutedEventArgs e) => _ = RevealAsync(elevated: true);

    private async Task RevealAsync(bool elevated)
    {
        if (_item is null || CurrentTarget is not { } target)
            return;
        var generation = _generation;
        var item = _item;
        SetBusy(true);
        ValueError.Visibility = Visibility.Collapsed;
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
        MaskedValue.Visibility = Visibility.Collapsed;
        HideButton.Visibility = Visibility.Visible;
        CopyValueButton.IsEnabled = true;

        var pairs = TryParseJsonObject(value);
        if (pairs is not null)
        {
            JsonGrid.ItemsSource = pairs;
            JsonGrid.Visibility = Visibility.Visible;
            ValueText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ValueText.Text = value;
            ValueText.Visibility = Visibility.Visible;
            JsonGrid.Visibility = Visibility.Collapsed;
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

    private void OnHide(object sender, RoutedEventArgs e) => Wipe();

    public void Wipe()
    {
        _hideTimer.Stop();
        _value = null;
        ValueText.Text = "";
        JsonGrid.ItemsSource = null;
        ValueText.Visibility = Visibility.Collapsed;
        JsonGrid.Visibility = Visibility.Collapsed;
        MaskedValue.Visibility = Visibility.Visible;
        HideButton.Visibility = Visibility.Collapsed;
        CopyValueButton.IsEnabled = false;
        Countdown.Text = "";
        SetBusy(false);
    }

    private void OnCopyValue(object sender, RoutedEventArgs e)
    {
        if (_value is not null)
            CopyValue(_value);
    }

    private void OnCopyJsonValue(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value })
            CopyValue(value);
    }

    private void CopyValue(string value)
    {
        if (IsSensitive)
        {
            SecureClipboard.CopySecret(value);
            Countdown.Text = $"copied — clipboard clears in {SecureClipboard.ClearAfter.TotalSeconds:0}s";
        }
        else
        {
            SecureClipboard.CopyPlain(value);
        }
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
            SecureClipboard.CopyPlain(_item.Name);
    }

    private void OnCopyArn(object sender, RoutedEventArgs e)
    {
        if (_item?.Arn is not null)
            SecureClipboard.CopyPlain(_item.Arn);
    }

    private async void OnRefreshMetadata(object sender, RoutedEventArgs e)
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
        ValueError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        ValueBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.IsEnabled = !busy;
        RevealElevatedButton.IsEnabled = !busy;
    }
}
