using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using Skypeek.Aws;
using Skypeek.Core.Models;
using Skypeek.Desktop.Infrastructure;

namespace Skypeek.Desktop.Views;

/// <summary>One key of a JSON secret in the editor. Values show as dots until "Show" is on.</summary>
public sealed partial class KeyValueRow : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private string _value = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private bool _masked = true;

    public string Shown
    {
        get => Masked ? new string('•', Math.Min(12, Math.Max(6, Value.Length))) : Value;
        set
        {
            if (!Masked)
                Value = value;
        }
    }
}

/// <summary>What the editor hands back; <see cref="Value"/> is cleared by the caller right after use.</summary>
public sealed record SecretEdit(Target Target, string Name, string Value, string? Type, string? Tier, string? Description, string? KmsKeyId);

/// <summary>
/// Change the value of a secret or parameter, or create one. Sends nothing itself: the caller runs the gateway call,
/// which asks for approval. The value lives only in this window and is cleared when it closes (and on lock, which
/// closes every dialog).
/// </summary>
public partial class SecretEditorDialog : DialogWindow
{
    private readonly ObservableCollection<KeyValueRow> _rows = [];
    private readonly CatalogKind _kind;
    private readonly CatalogItem? _item;
    private readonly Func<Task<string?>>? _loadCurrent;

    public SecretEditorDialog() : this(CatalogKind.Secret, null, null, [], null) { }

    /// <param name="item">The item to edit; null to create one.</param>
    /// <param name="value">The value already revealed (to start from), or null.</param>
    /// <param name="targets">Targets to create in (those with an elevated key).</param>
    /// <param name="loadCurrent">Reads the current value with the elevated key (after approval).</param>
    public SecretEditorDialog(CatalogKind kind, CatalogItem? item, string? value, IReadOnlyList<Target> targets, Func<Task<string?>>? loadCurrent, Target? target = null)
    {
        InitializeComponent();
        Icon = IconRenderer.WindowIcon();
        _kind = kind;
        _item = item;
        _loadCurrent = loadCurrent;
        JsonGrid.ItemsSource = _rows;
        var what = kind == CatalogKind.Secret ? "secret" : "parameter";
        Title = item is null ? $"New {what}" : $"Edit {what}";
        Heading.Text = item is null ? $"New {what}" : item.Name;
        SubHeading.Text = item is null
            ? kind == CatalogKind.Secret ? "Secrets Manager charges $0.40 per secret per month." : "Parameter Store: Standard parameters are free."
            : kind == CatalogKind.Secret ? "Saving creates a new version (the previous value stays as AWSPREVIOUS)."
            : $"{item.Type} · {item.Tier ?? "Standard"} · version {item.Version}. Saving creates the next version; type, key and tier stay.";
        CreateFields.IsVisible = item is null;
        ParameterFields.IsVisible = kind == CatalogKind.Parameter;
        KmsLabel.Text = kind == CatalogKind.Secret ? "KMS key (optional; empty = aws/secretsmanager)" : "KMS key for SecureString (optional; empty = aws/ssm)";
        JsonMode.IsVisible = kind == CatalogKind.Secret;
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = target ?? targets.FirstOrDefault();
        LoadButton.IsVisible = item is not null && value is null && loadCurrent is not null;
        if (value is not null)
            SetValue(value);
        ValueBox.TextChanged += (_, _) => UpdateInfo();
        Closed += (_, _) => Clear();
        Opened += (_, _) => (item is null ? NameBox : (Control)ValueBox).Focus();
        UpdateInfo();
    }

    public SecretEdit? Result { get; private set; }

    private void SetValue(string value)
    {
        ValueBox.Text = value;
        // JSON objects of simple values edit nicely as key/value rows.
        if (_kind == CatalogKind.Secret && TryRows(value, out var rows))
        {
            _rows.Clear();
            foreach (var row in rows)
                _rows.Add(row);
            JsonMode.IsChecked = true;
        }
        UpdateInfo();
    }

    private bool TryRows(string text, out List<KeyValueRow> rows)
    {
        rows = [];
        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj || obj.Any(p => p.Value is JsonObject or JsonArray))
                return false;
            rows = obj.Select(p => new KeyValueRow
            {
                Key = p.Key,
                Value = p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString() ?? "",
                Masked = ShowToggle.IsChecked != true,
            }).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string CurrentValue()
    {
        if (JsonMode.IsChecked != true)
            return ValueBox.Text ?? "";
        var obj = new JsonObject();
        foreach (var row in _rows.Where(r => r.Key.Length > 0))
            obj[row.Key] = row.Value;
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private void UpdateInfo()
    {
        var value = CurrentValue();
        ValueInfo.Text = value.Length == 0 ? "" : $"{System.Text.Encoding.UTF8.GetByteCount(value):N0} bytes" + (JsonMode.IsChecked == true ? $" · {_rows.Count} key(s)" : "");
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (JsonMode.IsChecked == true)
        {
            var text = ValueBox.Text ?? "";
            if (text.Length > 0 && !TryRows(text, out var rows))
            {
                ShowError("The value is not a JSON object of simple values; edit it as text.");
                TextMode.IsChecked = true;
                return;
            }
            if (text.Length > 0 && TryRows(text, out var parsed))
            {
                _rows.Clear();
                foreach (var row in parsed)
                    _rows.Add(row);
            }
        }
        else if (sender == TextMode && TextMode.IsChecked == true && _rows.Count > 0)
        {
            ValueBox.Text = CurrentValueFromRows();
        }
        ValueBox.IsVisible = JsonMode.IsChecked != true;
        JsonPanel.IsVisible = JsonMode.IsChecked == true;
        UpdateInfo();
    }

    private string CurrentValueFromRows()
    {
        var obj = new JsonObject();
        foreach (var row in _rows.Where(r => r.Key.Length > 0))
            obj[row.Key] = row.Value;
        return obj.ToJsonString();
    }

    private void OnShowChanged(object? sender, RoutedEventArgs e)
    {
        var show = ShowToggle.IsChecked == true;
        ValueBox.PasswordChar = show ? '\0' : '•';
        ShowToggle.Content = show ? "Hide" : "Show";
        foreach (var row in _rows)
            row.Masked = !show;
        JsonGrid.IsReadOnly = !show;
    }

    private void OnAddRow(object? sender, RoutedEventArgs e)
    {
        ShowToggle.IsChecked = true;
        var row = new KeyValueRow { Key = $"key{_rows.Count + 1}", Masked = false };
        _rows.Add(row);
        JsonGrid.SelectedItem = row;
        UpdateInfo();
    }

    private void OnRemoveRow(object? sender, RoutedEventArgs e)
    {
        if (JsonGrid.SelectedItem is KeyValueRow row)
            _rows.Remove(row);
        UpdateInfo();
    }

    private async void OnLoadCurrent(object? sender, RoutedEventArgs e)
    {
        if (_loadCurrent is null)
            return;
        LoadButton.IsEnabled = false;
        try
        {
            if (await _loadCurrent() is { } value)
            {
                SetValue(value);
                LoadButton.IsVisible = false;
            }
        }
        finally
        {
            LoadButton.IsEnabled = true;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var value = CurrentValue();
        var target = _item is null ? TargetBox.SelectedItem as Target : null;
        var name = _item?.Name ?? NameBox.Text?.Trim() ?? "";
        var type = _item?.Type ?? (_kind == CatalogKind.Parameter ? (TypeBox.SelectedItem as ComboBoxItem)?.Content as string : null);
        var tier = _item?.Tier ?? ((TierBox.SelectedItem as ComboBoxItem)?.Tag as string);
        if (_item is null && target is null)
        {
            ShowError("Pick the target to create it in (it needs an elevated key).");
            return;
        }
        if (name.Length == 0)
        {
            ShowError("Enter a name.");
            return;
        }
        if (value.Length == 0)
        {
            ShowError("Enter a value.");
            return;
        }
        if (_kind == CatalogKind.Parameter && AwsGateway.ValidateParameterValue(value, type ?? "String", tier) is { } problem)
        {
            ShowError(problem);
            return;
        }
        if (_kind == CatalogKind.Secret && System.Text.Encoding.UTF8.GetByteCount(value) > 65536)
        {
            ShowError("A secret can hold at most 64 KB.");
            return;
        }
        Result = new SecretEdit(target ?? null!, name, value, type, tier, _item is null ? DescriptionBox.Text : null, _item is null ? KmsBox.Text?.Trim() : null);
        Finish(true);
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);

    /// <summary>The value leaves the window's controls when it closes.</summary>
    private void Clear()
    {
        ValueBox.Text = "";
        foreach (var row in _rows)
            row.Value = "";
        _rows.Clear();
    }
}
