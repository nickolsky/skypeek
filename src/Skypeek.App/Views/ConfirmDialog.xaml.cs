using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace Skypeek.App.Views;

/// <summary>Themed yes/no confirmation (replaces the unstyled Win32 MessageBox).</summary>
public partial class ConfirmDialog
{
    private ConfirmDialog(string heading, string message, string okText, bool danger)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (danger)
        {
            OkButton.Appearance = ControlAppearance.Danger;
            // Destructive: Enter should not confirm by accident.
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
        }
        SourceInitialized += (_, _) => RoundCorners();
        Loaded += (_, _) => (danger ? CancelButton : OkButton).Focus();
    }

    public static bool Ask(Window? owner, string heading, string message, string okText = "OK", bool danger = false)
    {
        var dialog = new ConfirmDialog(heading, message, okText, danger);
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Windows 11 rounded corners and shadow for the borderless window (ignored on Windows 10).</summary>
    private void RoundCorners()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
