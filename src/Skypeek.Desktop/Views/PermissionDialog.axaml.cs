using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Skypeek.Core;

namespace Skypeek.Desktop.Views;

/// <summary>
/// Asked before every elevated call and every non-read action. Deny is the default button; irreversible actions also
/// require typing or pasting a confirmation phrase (e.g. the instance ID) before Allow is enabled.
/// </summary>
public partial class PermissionDialog : DialogWindow
{
    private readonly string? _confirmPhrase;

    public PermissionDialog(ElevationRequest request)
    {
        InitializeComponent();
        Icon = IconRenderer.WindowIcon();
        _confirmPhrase = request.ConfirmPhrase;
        HeadingText.Text = request.Explanation is null ? "Use the elevated key?" : "Allow this action with the elevated key?";
        var parts = new List<string>();
        if (request.Explanation is { } why)
            parts.Add(why);
        parts.Add(request.Elevated
            ? request.Target.UsesSameKey
                ? "⚠ This target uses the SAME key for read-only and elevated access (an exception you confirmed in Settings). The call is recorded in the request log."
                : "This one call is signed with the elevated profile instead of the read-only one and recorded in the request log."
            : "It is signed with the read-only profile and recorded in the request log.");
        ExplanationText.Text = string.Join("\n\n", parts);
        OperationText.Text = request.Operation;
        ResourceText.Text = request.Resource;
        TargetText.Text = $"{request.Target.DisplayName} · {request.Target.Region}";
        ProfileText.Text = request.Profile;
        AccountText.Text = $"{request.AccountId ?? "?"}{(request.RoleName is null ? "" : $" · role {request.RoleName}")}";

        if (_confirmPhrase is not null)
        {
            ConfirmPanel.IsVisible = true;
            PhraseText.Text = _confirmPhrase;
            AllowButton.IsEnabled = false;
            AllowButton.Classes.Remove("caution");
            AllowButton.Classes.Add("danger");
        }
        Opened += (_, _) => (_confirmPhrase is null ? (Control)DenyButton : ConfirmBox).Focus();
    }

    /// <summary>Copies the phrase and puts the cursor in the box, so Ctrl+V is all that is left.</summary>
    private void OnCopyPhrase(object? sender, RoutedEventArgs e)
    {
        if (_confirmPhrase is null)
            return;
        SecureClipboard.CopyPlain(_confirmPhrase);
        CopyPhraseButton.Content = "Copied";
        ConfirmBox.Focus();
    }

    private void OnConfirmChanged(object? sender, TextChangedEventArgs e) =>
        AllowButton.IsEnabled = string.Equals((ConfirmBox.Text ?? "").Trim(), _confirmPhrase, StringComparison.Ordinal);

    private void OnAllow(object? sender, RoutedEventArgs e)
    {
        if (_confirmPhrase is not null && (ConfirmBox.Text ?? "").Trim() != _confirmPhrase)
            return;
        Finish(true);
    }

    private void OnDeny(object? sender, RoutedEventArgs e) => Finish(false);
}
