using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Skypeek.Core.Logging;

namespace Skypeek.App.Views;

public partial class RequestLogView
{
    private const int MaxRows = 5000;
    private readonly RequestLogService _log;
    private readonly ObservableCollection<RequestLogEntry> _rows;
    private readonly ICollectionView _view;

    public RequestLogView(RequestLogService log)
    {
        InitializeComponent();
        _log = log;
        _rows = new ObservableCollection<RequestLogEntry>(log.Snapshot(MaxRows));
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        Grid.ItemsSource = _view;
        log.EntryAdded += OnEntryAdded;
    }

    public void Detach() => _log.EntryAdded -= OnEntryAdded;

    private void OnEntryAdded(RequestLogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        if (Follow.IsChecked != true)
            return;
        _rows.Insert(0, entry);
        while (_rows.Count > MaxRows)
            _rows.RemoveAt(_rows.Count - 1);
    });

    private bool Matches(object obj)
    {
        if (obj is not RequestLogEntry e)
            return false;
        if (ProblemsOnly.IsChecked == true && e.Outcome == RequestOutcome.Success)
            return false;
        if (ElevatedOnly.IsChecked == true && !e.Elevated)
            return false;
        var text = Filter.Text;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        return new[] { e.Profile, e.Region, e.Service, e.Operation, e.Parameters, e.ErrorCode, e.Message, e.AccountId }
            .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _view?.Refresh();
}
