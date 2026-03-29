using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using AzureBackup.ViewModels;
using AzureBackup.Views;

namespace AzureBackup;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindowViewModel? _viewModel;
    private Window? _mainWindow;

    /// <summary>
    /// Whether the system tray is available on this platform.
    /// GNOME on Linux removed tray support; fallback to normal minimize.
    /// </summary>
    private static bool IsTraySupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        IsLinuxTrayAvailable();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            _viewModel = new MainWindowViewModel();
            _mainWindow = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = _mainWindow;

            if (IsTraySupported)
            {
                // Keep running when window is closed (minimize to tray)
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ConfigureTrayIcon(desktop);
                _mainWindow.Closing += OnMainWindowClosing;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Configures the tray icon: wires menu commands and tooltip binding.
    /// </summary>
    private void ConfigureTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icons = TrayIcon.GetIcons(this);
        _trayIcon = icons?.FirstOrDefault();
        if (_trayIcon == null) return;

        // Wire "Show" menu item
        if (_trayIcon.Menu is NativeMenu menu)
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
            {
                if (item.Header == "Show Azure Backup")
                    item.Click += (_, _) => ShowMainWindow();
                else if (item.Header == "Exit")
                    item.Click += (_, _) => desktop.Shutdown();
            }
        }

        // Double-click on tray icon restores the window
        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        // Bind tooltip to ViewModel's TrayTooltipText
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _trayIcon.ToolTipText = _viewModel.TrayTooltipText;
        }
    }

    /// <summary>
    /// Updates the tray tooltip when the ViewModel's TrayTooltipText changes.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.TrayTooltipText) && _trayIcon != null && _viewModel != null)
        {
            _trayIcon.ToolTipText = _viewModel.TrayTooltipText;
        }
    }

    /// <summary>
    /// Intercepts window close to hide to tray instead.
    /// </summary>
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window window)
        {
            e.Cancel = true;
            window.Hide();
        }
    }

    /// <summary>
    /// Restores the main window from tray.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>
    /// Checks whether a system tray is likely available on Linux.
    /// KDE, XFCE, and other DEs that support StatusNotifierItem or XEmbed
    /// typically set XDG_CURRENT_DESKTOP to values other than "GNOME".
    /// </summary>
    private static bool IsLinuxTrayAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        // GNOME removed native tray support; other DEs generally support it
        return !desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase);
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}