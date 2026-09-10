using System;
using System.ComponentModel;
using System.Windows;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Interaction logic for DebugConsoleWindow.xaml.
/// </summary>
public partial class DebugConsoleWindow : Window
{
    private readonly DebugConsoleViewModel _viewModel;

    public DebugConsoleWindow(DebugConsoleViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.RequestScrollToEnd = () =>
        {
            if (LogsListBox.Items.Count > 0)
            {
                LogsListBox.ScrollIntoView(LogsListBox.Items[^1]);
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
