using System.Globalization;
using System.Windows;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

public partial class ThresholdDialog
{
    /// <param name="memoryLabel">What the second pair measures (e.g. "Connections % of max" for RDS).</param>
    public ThresholdDialog(string resource, ThresholdSettings current, bool hasOverride, string cpuLabel = "CPU", string memoryLabel = "Memory")
    {
        InitializeComponent();
        ResourceText.Text = resource;
        CpuLabel.Text = cpuLabel;
        MemLabel.Text = memoryLabel;
        CpuWarn.Text = current.CpuWarn.ToString(CultureInfo.InvariantCulture);
        CpuCrit.Text = current.CpuCritical.ToString(CultureInfo.InvariantCulture);
        MemWarn.Text = current.MemWarn.ToString(CultureInfo.InvariantCulture);
        MemCrit.Text = current.MemCritical.ToString(CultureInfo.InvariantCulture);
        ClearButton.Visibility = hasOverride ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>New override, or null to remove the override.</summary>
    public ThresholdSettings? Result { get; private set; }

    public static bool TryParseThresholds(string cpuWarn, string cpuCrit, string memWarn, string memCrit, out ThresholdSettings result, out string? error)
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryParseThresholds(CpuWarn.Text, CpuCrit.Text, MemWarn.Text, MemCrit.Text, out var result, out var error))
        {
            Error.Text = error;
            Error.Visibility = Visibility.Visible;
            return;
        }
        Result = result;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
