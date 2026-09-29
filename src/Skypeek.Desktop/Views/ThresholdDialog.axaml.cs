using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using System.Globalization;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

public partial class ThresholdDialog : DialogWindow
{
    private readonly bool _withStorage;

    /// <param name="memoryLabel">What the second pair measures (e.g. "Connections % of max" for RDS).</param>
    /// <param name="storage">RDS: the used-storage thresholds in effect; shows a third row.</param>
    public ThresholdDialog(string resource, ThresholdSettings current, bool hasOverride, string cpuLabel = "CPU", string memoryLabel = "Memory",
        (double Warn, double Critical)? storage = null)
    {
        InitializeComponent();
        ResourceText.Text = resource;
        CpuLabel.Text = cpuLabel;
        MemLabel.Text = memoryLabel;
        CpuWarn.Text = current.CpuWarn.ToString(CultureInfo.InvariantCulture);
        CpuCrit.Text = current.CpuCritical.ToString(CultureInfo.InvariantCulture);
        MemWarn.Text = current.MemWarn.ToString(CultureInfo.InvariantCulture);
        MemCrit.Text = current.MemCritical.ToString(CultureInfo.InvariantCulture);
        ClearButton.IsVisible = hasOverride;
        if (storage is { } st)
        {
            _withStorage = true;
            StorageLabel.IsVisible = StorageWarn.IsVisible = StorageCrit.IsVisible = true;
            StorageWarn.Text = st.Warn.ToString(CultureInfo.InvariantCulture);
            StorageCrit.Text = st.Critical.ToString(CultureInfo.InvariantCulture);
        }
        Opened += (_, _) => CpuWarn.Focus();
    }

    /// <summary>New override, or null to remove the override.</summary>
    public ThresholdSettings? Result { get; private set; }

    public static bool TryParseThresholds(string? cpuWarn, string? cpuCrit, string? memWarn, string? memCrit, out ThresholdSettings result, out string? error)
    {
        result = ThresholdSettings.Default;
        error = null;
        var values = new[] { cpuWarn, cpuCrit, memWarn, memCrit }
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
            .ToArray();
        if (values.Any(v => double.IsNaN(v) || v <= 0 || v > 100))
        {
            error = "Enter numbers between 1 and 100.";
            return false;
        }
        if (values[0] > values[1] || values[2] > values[3])
        {
            error = "Warning must not exceed critical.";
            return false;
        }
        result = new ThresholdSettings(values[0], values[1], values[2], values[3]);
        return true;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!TryParseThresholds(CpuWarn.Text, CpuCrit.Text, MemWarn.Text, MemCrit.Text, out var result, out var error))
        {
            ShowError(error);
            return;
        }
        if (_withStorage)
        {
            // Same rules as a CPU pair: 1–100, warning not above critical.
            if (!TryParseThresholds(StorageWarn.Text, StorageCrit.Text, StorageWarn.Text, StorageCrit.Text, out var storage, out var storageError))
            {
                ShowError($"Storage: {storageError}");
                return;
            }
            result = result with { StorageWarn = storage.CpuWarn, StorageCritical = storage.CpuCritical };
        }
        Result = result;
        Finish(true);
    }

    private void ShowError(string? error)
    {
        Error.Text = error;
        Error.IsVisible = true;
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Finish(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);
}
