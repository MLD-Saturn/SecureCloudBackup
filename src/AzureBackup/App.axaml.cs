using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    /// B49: gate for the two-pass <see cref="IClassicDesktopStyleApplicationLifetime.ShutdownRequested"/>
    /// dance. The first pass cancels the shutdown, awaits
    /// <see cref="MainWindowViewModel.DisposeAsync"/> (which stops the
    /// hourly checkpoint timer, runs a final
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, and disposes the
    /// <see cref="LocalDatabaseService"/> / SQLCipher connection), then
    /// re-issues the shutdown. The second pass observes this flag and
    /// allows the lifetime to tear down. Without this gate every clean
    /// exit (tray Exit, last-window-close on platforms with no tray, or
    /// any other <c>desktop.Shutdown()</c> caller) bypassed view-model
    /// disposal, leaving the SQLCipher connection un-closed and the WAL
    /// un-checkpointed.
    /// </summary>
    private bool _shutdownDisposeCompleted;

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

            // B49: every classic-desktop shutdown path (tray Exit,
            // last-window-close on platforms with no tray, OS-initiated
            // logoff that lets Avalonia finish its lifetime, etc.) ends
            // up firing ShutdownRequested. Hooking it here -- BEFORE the
            // tray-supported branch below -- guarantees the catalog
            // database is closed cleanly regardless of whether the tray
            // is configured.
            desktop.ShutdownRequested += OnShutdownRequested;

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
    /// B49: ensure the catalog database is closed cleanly on every
    /// shutdown path. The handler runs a two-pass dance because
    /// <see cref="MainWindowViewModel.DisposeAsync"/> is async and
    /// <see cref="ShutdownRequestedEventArgs"/> exposes a synchronous
    /// <see cref="ShutdownRequestedEventArgs.Cancel"/> only. First pass:
    /// cancel the shutdown, fire-and-forget the dispose continuation
    /// that re-invokes <c>desktop.Shutdown()</c> when disposal completes.
    /// Second pass: observe <see cref="_shutdownDisposeCompleted"/> and
    /// allow the lifetime to tear down.
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_shutdownDisposeCompleted || _viewModel is null ||
            sender is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        e.Cancel = true;
        _ = ShutdownAfterDisposeAsync(desktop);
    }

    private async Task ShutdownAfterDisposeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (_viewModel is { } vm)
            {
                await vm.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // Disposal must never block shutdown. Any failure has been
            // best-effort logged inside DisposeAsync; we still need to
            // re-issue the shutdown so the user's exit click takes effect.
        }
        finally
        {
            _shutdownDisposeCompleted = true;
            _viewModel = null;
            desktop.Shutdown();
        }
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