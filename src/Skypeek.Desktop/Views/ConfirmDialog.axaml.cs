using Avalonia.Controls;
using Avalonia.Interactivity;
using Skypeek.Desktop.Infrastructure;

namespace Skypeek.Desktop.Views;

/// <summary>Themed yes/no confirmation (and plain information messages).</summary>
public partial class ConfirmDialog : DialogWindow
{
    public ConfirmDialog() => InitializeComponent();

    private ConfirmDialog(string heading, string message, string okText, bool danger, bool information) : this()
    {
        Icon = IconRenderer.WindowIcon();
        HeadingText.Text = heading;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (danger)
        {
            OkButton.Classes.Remove("accent");
            OkButton.Classes.Add("danger");
            // Destructive: Enter should not confirm by accident.
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
        }
        if (information)
        {
            CancelButton.IsVisible = false;
            Grid.SetColumnSpan(OkButton, 3);
        }
        Opened += (_, _) => (danger ? CancelButton : OkButton).Focus();
    }

    public static Task<bool> AskAsync(Window? owner, string heading, string message, string okText = "OK", bool danger = false) =>
        Dialogs.ShowAsync(new ConfirmDialog(heading, message, okText, danger, information: false), owner);

    public static Task InformAsync(Window? owner, string heading, string message) =>
        Dialogs.ShowAsync(new ConfirmDialog(heading, message, "OK", danger: false, information: true), owner);

    private void OnOk(object? sender, RoutedEventArgs e) => Finish(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);
}
