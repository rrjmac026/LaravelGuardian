using System.Windows;
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
}