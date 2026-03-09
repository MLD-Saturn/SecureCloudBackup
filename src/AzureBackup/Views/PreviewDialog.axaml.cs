using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AzureBackup.Views;

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
}
