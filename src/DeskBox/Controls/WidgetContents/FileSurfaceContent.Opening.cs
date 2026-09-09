using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private readonly FileOpenRequestGate _openRequestGate = new();
    private long _openStateGeneration;

    private string OpeningStatusText =>
        string.Concat(T("Widget.Open"), "…");

    private async Task OpenFileItemAsync(WidgetItem item)
    {
        // Keep the gate identity stable even if the item is renamed while a
        // Shell request is still in flight.
        string requestPath = item.Path;
        if (!TryBeginOpenItem(requestPath))
        {
            PerformanceLogger.Mark(
                "FileSurface.OpenItem.DuplicateSuppressed",
                $"widget={WidgetId} kind={GetOpenItemKind(item)}");
            return;
        }

        long generation = _openStateGeneration;
        long stackPopoverGeneration = _stackPopoverShowGeneration;
        string kind = GetOpenItemKind(item);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using IDisposable timing = PerformanceLogger.Measure(
            "FileSurface.OpenItem",
            $"widget={WidgetId} kind={kind}");

        bool dispatched = false;
        try
        {
            SetOpeningVisual(item, isOpening: true);
            PerformanceLogger.Mark(
                "FileSurface.OpenItem.Requested",
                $"widget={WidgetId} kind={kind}");

            // Give the item badge and the pressed state a dispatcher turn to
            // render before any provider or Shell call begins. A toast alone
            // is not sufficient because a synchronous call can otherwise
            // block the same UI queue before the toast is painted.
            await Task.Yield();

            FileService.OpenItemResult result = await ViewModel.OpenItemAsync(
                item,
                _hostWindowHandle,
                _lifetimeCancellation.Token);
            dispatched = result == FileService.OpenItemResult.OpenedOrHandled;

            if (_isDisposed || generation != _openStateGeneration)
            {
                return;
            }

            PerformanceLogger.Mark(
                "FileSurface.OpenItem.Completed",
                $"widget={WidgetId} kind={kind} result={result} " +
                $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");

            if (result == FileService.OpenItemResult.Failed)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemFailed"),
                    WidgetFeedbackSeverity.Error,
                    "file-open-failed"));
            }
            else if (result == FileService.OpenItemResult.Busy)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemBusy"),
                    WidgetFeedbackSeverity.Warning,
                    "file-open-busy"));
            }
            else if (result == FileService.OpenItemResult.OpenedOrHandled)
            {
                ClearOpenedItemSelection(item, stackPopoverGeneration);
                // This confirms that Windows accepted the dispatch. It does
                // not claim that the target application's cold start has
                // finished, which Shell does not expose reliably for all
                // associations (DDE, UWP, and single-instance handlers).
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemDispatched"),
                    WidgetFeedbackSeverity.Success,
                    "file-open-dispatched"));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation here is normally content disposal. Do not show an
            // error after the widget has already been torn down.
            if (!_isDisposed && generation == _openStateGeneration)
            {
                PerformanceLogger.Mark(
                    "FileSurface.OpenItem.Cancelled",
                    $"widget={WidgetId} kind={kind}");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileSurface] Open item failed widget={WidgetId} " +
                $"kind={kind}: {ex}");
            if (!_isDisposed && generation == _openStateGeneration)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemFailed"),
                    WidgetFeedbackSeverity.Error,
                    "file-open-failed"));
            }
        }
        finally
        {
            EndOpenItem(requestPath, dispatched);
            SetOpeningVisual(item, isOpening: false);
        }
    }

    private void ClearOpenedItemSelection(
        WidgetItem item,
        long stackPopoverGeneration)
    {
        // Only deselect the dispatched item: another file may have been
        // selected while Shell was busy. Do not clear a newly opened popover.
        ItemsGrid.SelectedItems.Remove(item);
        ItemsList.SelectedItems.Remove(item);
        ClearOpenedItemPointerFeedback(ItemsGrid, item);
        ClearOpenedItemPointerFeedback(ItemsList, item);
        if (stackPopoverGeneration == _stackPopoverShowGeneration)
        {
            _stackPopoverItemsView?.SelectedItems.Remove(item);
            if (_stackPopoverItemsView is { } popover)
            {
                ClearOpenedItemPointerFeedback(popover, item);
            }
        }

        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
    }

    private void ClearOpenedItemPointerFeedback(ListViewBase view, WidgetItem item)
    {
        // Resolve through the owning view, not the shared surface registry:
        // an old completion must not reset a later popover or recycled item.
        if (view.ContainerFromItem(item) is not SelectorItem container ||
            FindDescendantByTag(container, "InteractiveSurface") is not Border border ||
            FileItemSurface.FindOwner(border) is not { } surface ||
            !ReferenceEquals(surface.DataContext, item))
        {
            return;
        }

        surface.ClearPointerFeedbackAfterOpen();
        // Commit the final unselected state even when pointer feedback was
        // already Normal and therefore raises no VisualStateChanged event.
        ApplyItemSurfaceVisual(border, surface.VisualState);
    }

    private bool TryBeginOpenItem(string path)
    {
        uint doubleClickTimeMs = Win32Helper.GetDoubleClickTime();
        if (doubleClickTimeMs == 0)
        {
            doubleClickTimeMs = 500;
        }

        return _openRequestGate.TryBegin(
            path,
            Environment.TickCount64,
            doubleClickTimeMs);
    }

    private void EndOpenItem(string path, bool dispatched)
    {
        _openRequestGate.Complete(path, dispatched);
    }

    private void SetOpeningVisual(WidgetItem item, bool isOpening)
    {
        if (_isDisposed)
        {
            return;
        }

        string status = isOpening ? OpeningStatusText : string.Empty;
        foreach (Border border in _itemSurfaces.ToArray())
        {
            FileItemSurface? surface = FileItemSurface.FindOwner(border);
            if (surface?.DataContext is WidgetItem surfaceItem &&
                ReferenceEquals(surfaceItem, item))
            {
                surface.SetOpeningState(isOpening, status);
            }
        }
    }

    private void ApplyOpeningStateToSurface(FileItemSurface surface)
    {
        if (surface.DataContext is not WidgetItem item)
        {
            return;
        }

        bool isOpening = _openRequestGate.IsActive(item.Path);
        surface.SetOpeningState(
            isOpening,
            isOpening ? OpeningStatusText : string.Empty);
    }

    private void ItemSurface_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (!_isDisposed && sender is FileItemSurface surface)
        {
            ApplyOpeningStateToSurface(surface);
        }
    }

    private void ResetOpenItemStateForReuse()
    {
        _openStateGeneration++;
        // An in-flight worker may still complete after this surface is reused.
        // Keep its active path until that completion so a recycled container
        // cannot start a second Shell request for the same item. Only the
        // short duplicate-click history is reset for the new presentation.
        _openRequestGate.ClearRecent();
        foreach (Border border in _itemSurfaces.ToArray())
        {
            if (FileItemSurface.FindOwner(border) is { } surface)
            {
                ApplyOpeningStateToSurface(surface);
            }
        }
    }

    private void ClearOpenItemStateForDispose()
    {
        _openStateGeneration++;
        _openRequestGate.Clear();
    }

    private static string GetOpenItemKind(WidgetItem item)
    {
        if (item.IsShortcut || ShortcutHelper.IsShortcutPath(item.Path))
        {
            return "shortcut";
        }

        return item.Path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "unc"
            : "filesystem";
    }
}
