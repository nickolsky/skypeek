using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>"Cost" row of a resource's details: the list-price estimate and billed cost, when enabled for the target.</summary>
public sealed class CostLine : ContentControl
{
    public static readonly DependencyProperty ResourceProperty = DependencyProperty.Register(
        nameof(Resource), typeof(ResourceStatus), typeof(CostLine), new PropertyMetadata(null, (d, _) => ((CostLine)d).Update()));

    public ResourceStatus? Resource
    {
        get => (ResourceStatus?)GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    private void Update()
    {
        if (Resource is not { } r || App.Current.Session?.Costs.For(r) is not { } cost)
        {
            Content = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = new TextBlock { Text = "Cost", Width = 110, Style = (Style)FindResource("Caption") };
        DockPanel.SetDock(label, Dock.Left);
        panel.Children.Add(label);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Body") };
        if (cost.Short is { } headline)
            text.Inlines.Add(new System.Windows.Documents.Run(headline + "  ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        text.Inlines.Add(new System.Windows.Documents.Run(cost.Detail) { Foreground = (Brush)FindResource("TextMuted") });
        panel.Children.Add(text);
        Content = panel;
    }
}
