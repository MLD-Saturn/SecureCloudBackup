using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using AzureBackup.ViewModels;

namespace AzureBackup.Views;

public partial class LogsView : UserControl
{
    private const double MinFontSize = 8;
    private const double MaxFontSize = 28;
    private const double FontSizeStep = 2;

    public LogsView()
    {
        InitializeComponent();

        CopyMenuItem.Click += (_, _) => CopySelectedLogLines();

        // Ctrl+C only needs ListBox focus (requires selection context)
        LogListBox.KeyDown += OnLogListBoxKeyDown;

        // Zoom works anywhere on the Logs tab, no ListBox focus needed
        KeyDown += OnViewKeyDown;
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        if (e.Key is Key.OemPlus or Key.OemMinus && DataContext is MainWindowViewModel vm)
        {
            var newSize = vm.LogFontSize + (e.Key == Key.OemPlus ? FontSizeStep : -FontSizeStep);
            vm.LogFontSize = Math.Clamp(newSize, MinFontSize, MaxFontSize);
            e.Handled = true;
        }
    }

    private void OnLogListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopySelectedLogLines();
            e.Handled = true;
        }
    }

    private async void CopySelectedLogLines()
    {
        try
        {
            var selectedLines = LogListBox.SelectedItems?
                .Cast<string>()
                .ToArray();

            if (selectedLines is not { Length: > 0 })
                return;

            var text = string.Join(Environment.NewLine, selectedLines);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error copying log lines: {ex}");
        }
    }
}
