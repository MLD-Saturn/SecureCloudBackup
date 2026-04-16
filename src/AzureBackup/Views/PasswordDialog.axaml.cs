using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AzureBackup.Views;

/// <summary>
/// Dialog for entering the unlock password on application startup.
/// Exposes the entered password as a caller-owned <see cref="char"/>[] that can be
/// zeroed after use, so the plaintext does not linger on the managed heap.
/// </summary>
public partial class PasswordDialog : Window
{
    /// <summary>
    /// Returns a freshly-allocated <see cref="char"/>[] copy of the password entered
    /// by the user. The caller owns the array and must clear it
    /// (e.g. with <see cref="Array.Clear(Array)"/>) as soon as it is no longer needed.
    /// Returns an empty array when no password is entered.
    /// </summary>
    public char[] TakePassword()
    {
        var text = PasswordBox.Text;
        if (string.IsNullOrEmpty(text))
            return Array.Empty<char>();

        var buffer = new char[text.Length];
        text.AsSpan().CopyTo(buffer);
        // Best-effort: clear the textbox so the string reference drops out of the UI
        // tree quickly. The original string in the intern/heap is still GC-reclaimable
        // only — char[] is the actual zero-able copy the caller receives.
        PasswordBox.Text = string.Empty;
        return buffer;
    }

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
        if (result && string.IsNullOrWhiteSpace(PasswordBox.Text))
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

