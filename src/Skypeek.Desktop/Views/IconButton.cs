using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Skypeek.Desktop.Views;

/// <summary>A normal button with a Fluent System icon before its text (like WPF-UI's Button Icon).</summary>
public class IconButton : Button
{
    public static readonly StyledProperty<Symbol> SymbolProperty = AvaloniaProperty.Register<IconButton, Symbol>(nameof(Symbol));

    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public IconButton()
    {
        // The text stays the Content (code may change it, e.g. "Copied"); the template adds the icon.
        ContentTemplate = new FuncDataTemplate<object?>((_, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new SymbolIcon
            {
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                [!SymbolIcon.SymbolProperty] = new Binding(nameof(Symbol)) { Source = this },
            });
            panel.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, [!TextBlock.TextProperty] = new Binding(".") });
            return panel;
        }, supportsRecycling: true);
    }
}
