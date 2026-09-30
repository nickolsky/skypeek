using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Skypeek.Desktop.Views;

/// <summary>
/// <c>views:SearchBoxClear.Enabled="True"</c> on a TextBox adds an X inside its right edge that clears the text. Unlike
/// Fluent's clearButton class it shows whenever there is text, not only while the box has focus.
/// </summary>
public static class SearchBoxClear
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(SearchBoxClear));

    public static bool GetEnabled(TextBox box) => box.GetValue(EnabledProperty);
    public static void SetEnabled(TextBox box, bool value) => box.SetValue(EnabledProperty, value);

    static SearchBoxClear()
    {
        EnabledProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.NewValue is not true)
                return;
            var button = new Button
            {
                Classes = { "transparent" },
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Arrow),
                Focusable = false,
                IsVisible = !string.IsNullOrEmpty(box.Text),
                Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 12 },
            };
            ToolTip.SetTip(button, "Clear");
            button.Click += (_, _) =>
            {
                box.Text = "";
                box.Focus();
            };
            box.InnerRightContent = button;
            box.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBox.TextProperty)
                    button.IsVisible = !string.IsNullOrEmpty(box.Text);
            };
        });
    }
}
