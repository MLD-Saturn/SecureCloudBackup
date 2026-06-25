using SecureCloudBackup.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SecureCloudBackup.Views;

/// <summary>
/// Dialog window for previewing operations before execution.
/// </summary>
public partial class PreviewDialog : Window
{
    public PreviewDialog()
    {
        InitializeComponent();
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    /// <summary>
    /// Handles individual file checkbox clicks to refresh summary stats.
    /// </summary>
    private void FileCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OperationPreviewViewModel vm)
        {
            vm.RefreshInclusionState();
        }
    }
}
