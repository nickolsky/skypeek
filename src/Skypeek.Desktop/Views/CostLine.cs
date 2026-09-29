using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>"Cost" row of a resource's details: the list-price estimate and billed cost, when enabled for the target.</summary>
public sealed class CostLine : ContentControl
{
    public static readonly StyledProperty<ResourceStatus?> ResourceProperty = AvaloniaProperty.Register<CostLine, ResourceStatus?>(nameof(Resource));

    public ResourceStatus? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResourceProperty)
            Update();
    }

    private void Update()
    {
        if (Resource is not { } r || App.Current.Session?.Costs.For(r) is not { } cost)
        {
            Content = null;
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = new TextBlock { Text = "Cost", Classes = { "label" } };
        DockPanel.SetDock(label, Dock.Left);
        panel.Children.Add(label);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" }, Inlines = [] };
        if (cost.Short is { } headline)
            text.Inlines!.Add(new Run(headline + "  ") { FontWeight = FontWeight.SemiBold, Foreground = Brush("TextStrong") });
        text.Inlines!.Add(new Run(cost.Detail) { Foreground = Brush("TextMuted") });
        panel.Children.Add(text);
        Content = panel;
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, out var value) ? value as IBrush : null;
}
