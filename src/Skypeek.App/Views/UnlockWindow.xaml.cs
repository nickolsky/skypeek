using System.Windows;
using System.Windows.Input;
using Skypeek.App.Infrastructure;

namespace Skypeek.App.Views;

public enum UnlockMode
{
    Create,
    Unlock,
    ReAuth,
}

public partial class UnlockWindow
{
    private const int MinPasswordLength = 8;
    private readonly Func<UnlockMode, string, Task<string?>> _attempt;
    private UnlockMode _mode;
    private bool _succeeded;

    public UnlockWindow(UnlockMode mode, Func<UnlockMode, string, Task<string?>> attempt)
    {
        InitializeComponent();
        Icon = IconRenderer.WindowIcon();
        _attempt = attempt;
        SetMode(mode);
        Loaded += (_, _) => Password.Focus();
        Closed += (_, _) =>
        {
            if (_succeeded)
                App.Current.AfterUnlock(Then);
        };
    }

    public UiTarget Then { get; set; }

    private void SetMode(UnlockMode mode)
    {
        _mode = mode;
        Confirm.Visibility = ConfirmLabel.Visibility = mode == UnlockMode.Create ? Visibility.Visible : Visibility.Collapsed;
        ResetLink.Visibility = mode == UnlockMode.Create ? Visibility.Collapsed : Visibility.Visible;
        (Heading.Text, Explanation.Text, Submit.Content) = mode switch
        {
            UnlockMode.Create => ("Create master password",
                "All local data (settings, secret/parameter lists, health snapshots and the request log) is stored in an encrypted vault. " +
                "The password cannot be recovered — if you forget it, the vault is reset and lists are downloaded again.",
                "Create vault"),
            UnlockMode.Unlock => ($"Unlock {AppInfo.Name}", "Enter the master password to open the encrypted vault.", "Unlock"),
            _ => ("Locked", $"{AppInfo.Name} locked after inactivity. Background refresh keeps running; confirm it's you to continue.", "Unlock"),
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSubmit(sender, e);
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        var password = Password.Password;
        if (_mode == UnlockMode.Create)
        {
            if (password.Length < MinPasswordLength)
            {
                ShowError($"Use at least {MinPasswordLength} characters.");
                return;
            }
            if (password != Confirm.Password)
            {
                ShowError("Passwords do not match.");
                return;
            }
        }
        else if (password.Length == 0)
        {
            return;
        }

        SetBusy(true);
        string? error;
        try
        {
            error = await _attempt(_mode, password);
        }
        catch (Exception ex)
        {
            App.DebugLog(ex, "unlock");
            error = ex.Message;
        }
        SetBusy(false);

        if (error is not null)
        {
            ShowError(error);
            Password.Password = "";
            Password.Focus();
            return;
        }

        _succeeded = true;
        Close();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(this, "Reset the vault?",
                "This permanently deletes the encrypted vault: settings, targets, cached lists, health snapshots and the request log. " +
                "Secret and parameter lists are downloaded again after you add targets.", "Reset vault", danger: true))
            return;

        App.Current.ResetVault();
        Error.Visibility = Visibility.Collapsed;
        Password.Password = "";
        SetMode(UnlockMode.Create);
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Submit.IsEnabled = !busy;
        Password.IsEnabled = !busy;
        Confirm.IsEnabled = !busy;
    }
}
