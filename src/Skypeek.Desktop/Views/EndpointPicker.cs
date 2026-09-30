using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Skypeek.Desktop.Views;

/// <summary>
/// A search box that picks a reach endpoint: type part of a name, id or IP and choose from the matches, or type an IP
/// address or host name to use as it is.
/// </summary>
public sealed class EndpointPicker : UserControl
{
    private readonly TextBox _box = new() { PlaceholderText = "Name, id, IP address or host name…" };
    private readonly ListBox _list = new() { MaxHeight = 260, Background = Avalonia.Media.Brushes.Transparent };
    private readonly Popup _popup;
    private AppSession? _session;
    private IReadOnlyList<EndpointOption> _options = [];
    private bool _setting;

    public EndpointPicker()
    {
        SearchBoxClear.SetEnabled(_box, true);
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EndpointOption>((o, _) =>
        {
            var panel = new StackPanel { Margin = new Thickness(2, 1) };
            panel.Children.Add(new TextBlock { Text = o?.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = o?.Detail, Classes = { "caption" } });
            return panel;
        });
        _popup = new Popup
        {
            PlacementTarget = _box,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new Border { Classes = { "panel" }, Padding = new Thickness(4), Child = _list },
        };
        var root = new Panel();
        root.Children.Add(_box);
        root.Children.Add(_popup);
        Content = root;

        _box.TextChanged += (_, _) =>
        {
            if (_setting)
                return;
            Selected = null;
            Refresh();
        };
        _box.KeyDown += OnKeyDown;
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is EndpointOption option && !_setting)
                Pick(option);
        };
    }

    public EndpointOption? Selected { get; private set; }

    public event Action<EndpointOption>? Picked;

    public void Setup(AppSession session, IReadOnlyList<EndpointOption> options)
    {
        _session = session;
        _options = options;
    }

    public void FocusInput() => _box.Focus();

    /// <summary>Preselects the option for a dashboard resource or interface (by key).</summary>
    public void Select(string key)
    {
        if (_options.FirstOrDefault(o => o.Key == key) is { } option)
            Pick(option);
    }

    private void Refresh()
    {
        var text = _box.Text?.Trim() ?? "";
        if (text.Length == 0 || _session is null)
        {
            _popup.IsOpen = false;
            return;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = ReachEndpoints.ForText(_session, text)
            .Concat(_options.Where(o => words.All(w => $"{o.Title} {o.Detail} {o.Search}".Contains(w, StringComparison.OrdinalIgnoreCase))).Take(15))
            .ToList();
        _setting = true;
        _list.ItemsSource = matches;
        _list.SelectedItem = null;
        _setting = false;
        _list.Width = Math.Max(360, _box.Bounds.Width);
        _popup.IsOpen = matches.Count > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen || _list.ItemsSource is not IReadOnlyList<EndpointOption> items || items.Count == 0)
            return;
        if (e.Key == Key.Down)
        {
            _setting = true;
            _list.SelectedIndex = Math.Min(items.Count - 1, _list.SelectedIndex + 1);
            _setting = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _setting = true;
            _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
            _setting = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Pick(_list.SelectedItem as EndpointOption ?? items[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void Pick(EndpointOption option)
    {
        Selected = option;
        _setting = true;
        _box.Text = $"{option.Title}  ({option.Detail})";
        _setting = false;
        _popup.IsOpen = false;
        Picked?.Invoke(option);
    }
}
