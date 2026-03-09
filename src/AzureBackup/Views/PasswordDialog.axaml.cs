using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AzureBackup.Views;

/// <summary>
/// Dialog for entering the unlock password on application startup.
/// </summary>
public partial class PasswordDialog : Window
{
    /// <summary>
    /// Gets the password entered by the user.
    /// </summary>
    public string Password => PasswordBox.Text ?? string.Empty;

    public PasswordDialog()
    {
        InitializeComponent();
        
        // Wire up button events
        OkButton.Click += OnOkClick;
        CancelButton.Click += OnCancelClick;
        
        // Handle Enter key in password box
        PasswordBox.KeyDown += OnPasswordBoxKeyDown;
        
        // Focus password box when dialog opens
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnPasswordBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryClose(true);
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        TryClose(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        TryClose(false);
    }

    private void TryClose(bool result)
    {
        if (result && string.IsNullOrWhiteSpace(Password))
        {
            ShowError("Please enter a password");
            return;
        }
        
        Close(result);
    }

    /// <summary>
    /// Shows an error message in the dialog.
    /// </summary>
    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        PasswordBox.Focus();
        PasswordBox.SelectAll();
    }

    /// <summary>
    /// Clears the error message.
    /// </summary>
    public void ClearError()
    {
        ErrorText.IsVisible = false;
    }
}
