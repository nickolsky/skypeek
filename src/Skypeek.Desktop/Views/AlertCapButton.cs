using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentIcons.Common;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>
/// "Alerts: …" on a resource's details (the DataContext is the resource): caps how much it may alert, overriding the
/// target's setting — e.g. a dev environment that should never turn the tray red.
/// </summary>
public sealed class AlertCapButton : IconButton
{
    public AlertCapButton()
    {
        Symbol = Symbol.AlertSnooze;
        ToolTip.SetTip(this, "Maximum alert level of this resource (dev and sandbox resources need not turn the tray red)");
        Click += (_, _) => ShowMenu();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Content = Text(DataContext as ResourceStatus);
    }

    private static string Text(ResourceStatus? r) => r?.Cap switch
    {
        AlertCap.Warning => "Alerts: max warning",
        AlertCap.Info => "Alerts: info only",
        _ => "Alerts: normal",
    };

    public static string CapName(AlertCap cap) => cap switch
    {
        AlertCap.Warning => "max warning (never red)",
        AlertCap.Info => "info only (not counted, no notifications)",
        _ => "normal",
    };

    private void ShowMenu()
    {
        if (DataContext is not ResourceStatus r || (Application.Current as App)?.Session is not { } session)
            return;
        var settings = session.Settings.Settings;
        var target = session.Settings.FindTarget(r.TargetId);
        var own = settings.ResourceAlertCaps.TryGetValue(r.ResourceKey, out var set) ? set : (AlertCap?)null;

        var menu = new MenuFlyout();
        void Add(string header, AlertCap? cap)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = own == cap ? new FluentIcons.Avalonia.SymbolIcon { Symbol = Symbol.Checkmark, FontSize = 14 } : null,
            };
            item.Click += (_, _) => Apply(session, r, cap);
            menu.Items.Add(item);
        }
        Add($"Same as the target ({CapName(target?.AlertCap ?? AlertCap.None)})", null);
        menu.Items.Add(new Separator());
        Add("Normal: warnings and critical problems", AlertCap.None);
        Add("Max warning: never turns the tray red", AlertCap.Warning);
        Add("Info only: shown, never counted or notified", AlertCap.Info);
        menu.ShowAt(this);
    }

    private void Apply(AppSession session, ResourceStatus r, AlertCap? cap)
    {
        var settings = session.Settings.Settings;
        if (cap is { } c)
            settings.ResourceAlertCaps[r.ResourceKey] = c;
        else
            settings.ResourceAlertCaps.Remove(r.ResourceKey);
        session.Settings.SaveSettings(settings);
        session.Health.Reevaluate();
        Content = Text(r);
        RaiseEvent(new RoutedEventArgs(ChangedEvent, this));
    }

    public static readonly RoutedEvent<RoutedEventArgs> ChangedEvent =
        RoutedEvent.Register<AlertCapButton, RoutedEventArgs>("Changed", RoutingStrategies.Bubble);
}
