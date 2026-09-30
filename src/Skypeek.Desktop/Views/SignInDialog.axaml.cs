using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;

namespace Skypeek.Desktop.Views;

/// <summary>One SSO sign-in that ended: signing in with any of its profiles renews all of them.</summary>
/// <param name="Key">The token cache file (one per sso-session or start URL).</param>
public sealed record SignInItem(string Key, string LoginProfile, string Title, string Detail, IReadOnlyList<string> Profiles);

/// <summary>
/// "Your AWS sign-in has ended": shown on top of everything (also while only the tray icon is up) when an SSO sign-in
/// used by a target ends. Items leave the list by themselves once their sign-in is back.
/// </summary>
public partial class SignInDialog : DialogWindow
{
    private readonly ObservableCollection<SignInItem> _items = [];

    public SignInDialog()
    {
        InitializeComponent();
        Icon = IconRenderer.WindowIcon();
        Sessions.ItemsSource = _items;
    }

    public void Add(IEnumerable<SignInItem> items)
    {
        foreach (var item in items)
            if (_items.All(i => i.Key != item.Key))
                _items.Add(item);
    }

    /// <summary>Removes signed-in profiles; closes when nothing is left.</summary>
    public void Restored(IReadOnlyCollection<string> profiles)
    {
        foreach (var item in _items.Where(i => i.Profiles.Any(profiles.Contains)).ToList())
            _items.Remove(item);
        if (_items.Count == 0)
            Finish(true);
    }

    public int Count => _items.Count;

    private void OnSignIn(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not SignInItem item)
            return;
        Status.IsVisible = true;
        Status.Text = $"aws sso login --profile {item.LoginProfile}";
        var error = Shell.StartSsoLogin(item.LoginProfile, line => Dispatcher.UIThread.Post(() => Status.Text = $"{Status.Text}\n{line}"));
        Status.Text = error ?? $"{Status.Text}\nFinish signing in in the browser; this closes by itself.";
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Finish(false);
}
