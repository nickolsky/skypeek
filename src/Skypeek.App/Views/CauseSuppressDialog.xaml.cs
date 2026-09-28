using System.Windows;
using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

public partial class CauseSuppressDialog
{
    private readonly Target _target;
    private readonly string _environment;

    public CauseSuppressDialog(Target target, string environmentName, string cause)
    {
        InitializeComponent();
        _target = target;
        _environment = environmentName;
        Pattern.Text = HealthRules.NormalizeCause(cause);
        ThisEnv.Content = $"Only {environmentName}";
        ThisTarget.Content = $"All environments in {target.DisplayName} ({target.Region})";
        Loaded += (_, _) =>
        {
            Pattern.Focus();
            Pattern.SelectAll();
        };
    }

    public CauseSuppression? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var pattern = HealthRules.NormalizeCause(Pattern.Text ?? "");
        if (pattern.Replace("*", "").Replace("?", "").Trim().Length < 5)
        {
            Error.Text = "Enter the cause text or a pattern with enough fixed text (a bare * would hide every cause).";
            Error.Visibility = Visibility.Visible;
            return;
        }
        Result = AllTargets.IsChecked == true
            ? new CauseSuppression(pattern, null, null, DateTime.UtcNow)
            : ThisTarget.IsChecked == true
                ? new CauseSuppression(pattern, _target.Id, null, DateTime.UtcNow)
                : new CauseSuppression(pattern, _target.Id, _environment, DateTime.UtcNow);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
