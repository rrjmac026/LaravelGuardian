using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using LaravelGuardian.UI.ViewModels;

namespace LaravelGuardian.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.Activity.CollectionChanged += OnActivityChanged;
    }

    // A PasswordBox cannot be bound, so each account row syncs its box with RoleAccount.Password here.
    // Loaded: copy the saved/seeder password into the box. Changed: copy what is typed back to the row.
    private void RolePassword_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is RoleAccount account && box.Password != account.Password)
            box.Password = account.Password;
    }

    private void RolePassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is RoleAccount account && account.Password != box.Password)
            account.Password = box.Password;
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            bool atBottom = ActivityBox.VerticalOffset + ActivityBox.ViewportHeight
                            >= ActivityBox.ExtentHeight - 1;

            foreach (string line in e.NewItems)
                ActivityBox.AppendText(line + Environment.NewLine);

            if (atBottom) ActivityBox.ScrollToEnd();
        }
        else
        {
            // Clear / trimmed old lines: rebuild from the source
            ActivityBox.Text = string.Join(Environment.NewLine, _vm.Activity)
                               + (_vm.Activity.Count > 0 ? Environment.NewLine : "");
            ActivityBox.ScrollToEnd();
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Copy the selection if there is one, otherwise everything
            var text = string.IsNullOrEmpty(ActivityBox.SelectedText)
                ? ActivityBox.Text
                : ActivityBox.SelectedText;
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }
        catch { /* clipboard can be briefly locked by another app */ }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _vm.Activity.Clear();
}