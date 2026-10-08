using System.Data;
using System.Windows;
using System.Windows.Controls;
using LaravelGuardian.UI.ViewModels;

namespace LaravelGuardian.UI;

public partial class ResultsWindow : Window
{
    public ResultsWindow(ResultsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshCommand.ExecuteAsync(null);
    }

    // The matrix columns are generated from the data (one per role). Show the role label as header.
    private void MatrixGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is not DataGrid { ItemsSource: DataView view }) return;

        var caption = view.Table?.Columns[e.PropertyName]?.Caption ?? e.PropertyName;
        e.Column.Header = caption.ToUpperInvariant();

        if (e.PropertyName == "route")
        {
            e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            e.Column.MinWidth = 320;
        }
        else
        {
            e.Column.Width = 120;
        }
    }
}