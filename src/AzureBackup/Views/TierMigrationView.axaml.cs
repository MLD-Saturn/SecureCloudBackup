using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AzureBackup.Core.Models;
using AzureBackup.ViewModels;

namespace AzureBackup.Views;

public partial class TierMigrationView : UserControl
{
    private const string TierDragFormat = "AzureBackup.TierMigration";
    private Point _dragStartPoint;
    private bool _isDragging;

    public TierMigrationView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Forwards a log message to the ViewModel's log delegate.
    /// Compiled out in Release builds when DIAGNOSTICLOG is not defined.
    /// </summary>
    [Conditional("DIAGNOSTICLOG")]
    private static void Log(TierMigrationViewModel vm, string message) =>
        vm.LogMessage(message);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        WirePaneDragDrop("HotPane", StorageTier.Hot);
        WirePaneDragDrop("CoolPane", StorageTier.Cool);
        WirePaneDragDrop("ColdPane", StorageTier.Cold);
        WirePaneDragDrop("ArchivePane", StorageTier.Archive);

        WireListBoxDrag("HotListBox", StorageTier.Hot);
        WireListBoxDrag("CoolListBox", StorageTier.Cool);
        WireListBoxDrag("ColdListBox", StorageTier.Cold);
        WireListBoxDrag("ArchiveListBox", StorageTier.Archive);

        WireSelectButtons("SelectAllHotBtn", StorageTier.Hot, select: true);
        WireSelectButtons("DeselectAllHotBtn", StorageTier.Hot, select: false);
        WireSelectButtons("SelectAllCoolBtn", StorageTier.Cool, select: true);
        WireSelectButtons("DeselectAllCoolBtn", StorageTier.Cool, select: false);
        WireSelectButtons("SelectAllColdBtn", StorageTier.Cold, select: true);
        WireSelectButtons("DeselectAllColdBtn", StorageTier.Cold, select: false);
        WireSelectButtons("SelectAllArchiveBtn", StorageTier.Archive, select: true);
        WireSelectButtons("DeselectAllArchiveBtn", StorageTier.Archive, select: false);
    }

    private void WireSelectButtons(string buttonName, StorageTier tier, bool select)
    {
        var button = this.FindControl<Button>(buttonName);
        if (button == null) return;

        button.Click += (_, _) =>
        {
            if (DataContext is not TierMigrationViewModel vm) return;
            if (select)
                vm.SelectAll(tier);
            else
                vm.DeselectAll(tier);
        };
    }

    private void WireListBoxDrag(string listBoxName, StorageTier sourceTier)
    {
        var listBox = this.FindControl<ListBox>(listBoxName);
        if (listBox == null) return;

        listBox.AddHandler(PointerPressedEvent, (_, args) =>
        {
            _dragStartPoint = args.GetPosition(listBox);
            _isDragging = false;
        }, RoutingStrategies.Tunnel);

        listBox.AddHandler(PointerMovedEvent, async (_, args) =>
        {
            if (!args.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed) return;
            if (_isDragging) return;

            var position = args.GetPosition(listBox);
            var diff = position - _dragStartPoint;
            if (Math.Abs(diff.X) < 10 && Math.Abs(diff.Y) < 10) return;

            if (DataContext is not TierMigrationViewModel vm) return;
            var collection = vm.GetCollectionForTier(sourceTier);
            var selectedCount = collection.Count(f => f.IsSelected);
            if (selectedCount == 0) return;

            Log(vm, $"WireListBoxDrag: Drag initiated from {sourceTier} pane with {selectedCount} selected file(s)");
            _isDragging = true;
            var data = new DataObject();
            data.Set(TierDragFormat, sourceTier.ToString());
            await DragDrop.DoDragDrop(args, data, DragDropEffects.Move);
            _isDragging = false;
        }, RoutingStrategies.Tunnel);
    }

    private void WirePaneDragDrop(string paneName, StorageTier targetTier)
    {
        var pane = this.FindControl<Border>(paneName);
        if (pane == null) return;

        pane.AddHandler(DragDrop.DragOverEvent, (_, args) =>
        {
            if (args.Data.Contains(TierDragFormat))
            {
                var sourceTierStr = args.Data.Get(TierDragFormat) as string;
                if (Enum.TryParse<StorageTier>(sourceTierStr, out var source) && source != targetTier)
                {
                    args.DragEffects = DragDropEffects.Move;
                    pane.BorderThickness = new Thickness(3);
                    pane.BorderBrush = Avalonia.Media.Brushes.DodgerBlue;
                }
                else
                {
                    args.DragEffects = DragDropEffects.None;
                }
            }
            args.Handled = true;
        });

        pane.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            pane.BorderThickness = new Thickness(0);
            pane.BorderBrush = null;
        });

        pane.AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            pane.BorderThickness = new Thickness(0);
            pane.BorderBrush = null;

            if (!args.Data.Contains(TierDragFormat)) return;

            var sourceTierStr = args.Data.Get(TierDragFormat) as string;
            if (!Enum.TryParse<StorageTier>(sourceTierStr, out var sourceTier)) return;
            if (sourceTier == targetTier) return;

            if (DataContext is TierMigrationViewModel vm)
            {
                Log(vm, $"WirePaneDragDrop: Drop accepted — {sourceTier} → {targetTier}");
                await vm.MigrateSelectedAsync(sourceTier, targetTier);
            }

            args.Handled = true;
        });
    }
}
