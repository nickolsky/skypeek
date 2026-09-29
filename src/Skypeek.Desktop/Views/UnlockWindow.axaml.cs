
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
namespace Skypeek.Desktop.Views;

public enum UnlockMode
{
    Create,
    Unlock,
    ReAuth,
}

public partial class UnlockWindow : Window
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
        Opened += (_, _) => Password.Focus();
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
        Confirm.IsVisible = ConfirmLabel.IsVisible = mode == UnlockMode.Create;
        ResetLink.IsVisible = !(mode == UnlockMode.Create);
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSubmit(sender, e);
    }

    private async void OnSubmit(object? sender, RoutedEventArgs e)
    {
        var password = Password.Text ?? "";
        if (_mode == UnlockMode.Create)
        {
            if (password.Length < MinPasswordLength)
            {
                ShowError($"Use at least {MinPasswordLength} characters.");
                return;
            }
            if (password != Confirm.Text)
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
            Password.Text = "";
            Password.Focus();
            return;
        }

        _succeeded = true;
        Close();
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(this, "Reset the vault?",
                "This permanently deletes the encrypted vault: settings, targets, cached lists, health snapshots and the request log. " +
                "Secret and parameter lists are downloaded again after you add targets.", "Reset vault", danger: true))
            return;

        App.Current.ResetVault();
        Error.IsVisible = false;
        Password.Text = "";
        SetMode(UnlockMode.Create);
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.IsVisible = busy;
        Submit.IsEnabled = !busy;
        Password.IsEnabled = !busy;
        Confirm.IsEnabled = !busy;
    }
}
