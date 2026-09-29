using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace Skypeek.Desktop.Views;

/// <summary>"Hide" / "Unhide" for a resource's details (the DataContext is the resource).</summary>
public sealed class HideButton : IconButton
{
    public HideButton()
    {
        this[!ContentProperty] = new Binding("IsHidden") { Converter = new FuncValueConverter<bool, string>(hidden => hidden ? "Unhide" : "Hide") };
        this[!SymbolProperty] = new Binding("IsHidden") { Converter = new FuncValueConverter<bool, Symbol>(hidden => hidden ? Symbol.Eye : Symbol.EyeOff) };
        this[!ToolTip.TipProperty] = new Binding("IsHidden")
        {
            Converter = new FuncValueConverter<bool, string>(hidden => hidden
                ? "Show this resource on the dashboard again"
                : "Hide from the dashboard: not shown, not counted as a problem, no notifications or metric queries"),
        };
    }
}
