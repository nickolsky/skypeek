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

public partial class CauseSuppressDialog : DialogWindow
{
    private readonly Target _target;
    private readonly string _environment;
    private readonly string? _instance;

    /// <param name="instance">This occurrence (a failed build, a failed stack update): offers "only this failure".</param>
    public CauseSuppressDialog(Target target, string environmentName, string cause, string? instance = null, string? instanceLabel = null)
    {
        InitializeComponent();
        _target = target;
        _environment = environmentName;
        _instance = instance;
        Pattern.Text = HealthRules.NormalizeCause(cause);
        ThisEnv.Content = $"Only {environmentName}";
        if (instance is not null)
        {
            // A known failure: by default only this one, so the next failure alerts again.
            ThisFailure.IsVisible = true;
            ThisFailure.IsChecked = true;
            ThisFailure.Content = $"Only {instanceLabel ?? "this failure"} of {environmentName} — alert again if it fails again";
            ThisEnv.Content = $"Every failure of {environmentName}";
        }
        ThisTarget.Content = $"Everything in {target.DisplayName} ({target.Region})";
        Opened += (_, _) =>
        {
            Pattern.Focus();
            Pattern.SelectAll();
        };
    }

    public CauseSuppression? Result { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var pattern = HealthRules.NormalizeCause(Pattern.Text ?? "");
        if (pattern.Replace("*", "").Replace("?", "").Trim().Length < 5)
        {
            Error.Text = "Enter the cause text or a pattern with enough fixed text (a bare * would hide every cause).";
            Error.IsVisible = true;
            return;
        }
        Result = ThisFailure.IsChecked == true && _instance is not null
            ? new CauseSuppression(pattern, _target.Id, _environment, DateTime.UtcNow, OnlyFor: _instance)
            : AllTargets.IsChecked == true
            ? new CauseSuppression(pattern, null, null, DateTime.UtcNow)
            : ThisTarget.IsChecked == true
                ? new CauseSuppression(pattern, _target.Id, null, DateTime.UtcNow)
                : new CauseSuppression(pattern, _target.Id, _environment, DateTime.UtcNow);
        Finish(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);
}
