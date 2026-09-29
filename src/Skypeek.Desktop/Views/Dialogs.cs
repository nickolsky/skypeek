using Avalonia.Controls;

namespace Skypeek.Desktop.Views;

/// <summary>A window that closes with a yes/no result (Avalonia dialogs are asynchronous).</summary>
public class DialogWindow : Window
{
    public bool Confirmed { get; private set; }

    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>Closes the dialog with this result (WPF's DialogResult = …).</summary>
    public void Finish(bool result)
    {
        Confirmed = result;
        Close(result);
    }
}

public static class Dialogs
{
    /// <summary>
    /// Shows a dialog modally over <paramref name="owner"/> when it is visible, otherwise on its own (e.g. a permission
    /// prompt while only the tray icon is up), and returns its result.
    /// </summary>
    public static async Task<bool> ShowAsync(DialogWindow dialog, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await dialog.ShowDialog<object?>(owner);
            return dialog.Confirmed;
        }
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        dialog.Activate();
        await closed.Task;
        return dialog.Confirmed;
    }

    /// <summary>The window that hosts a control (owner for dialogs opened from it).</summary>
    public static Window? OwnerOf(Control control) => TopLevel.GetTopLevel(control) as Window;
}
