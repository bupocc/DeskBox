using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    public Task InitializeAsync()
    {
        return InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsLoading = true;
        try
        {
            EnsureFolderBackedConfig();
            cancellationToken.ThrowIfCancellationRequested();
            MappedFolderPath = Config.MappedFolderPath;
            _mappedFolderTraversalPath = ResolveMappedFolderTraversalPath();
            SetCurrentFolderPath(_mappedFolderTraversalPath);
            await ConfigureFolderWatchersAsync(CurrentFolderPath, cancellationToken);
            await ReloadFolderContentsAsync(
                CurrentFolderPath!,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsInitialized = !_isDisposed;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add file or folder items to the widget, or move/copy them into a mapped folder.
    /// </summary>
    [RelayCommand]
    public async Task AddItemsAsync(IEnumerable<string> paths)
    {
        await ImportPathsAsync(paths);
    }

    public async Task<IReadOnlyList<string>> ImportPathsAsync(
        IEnumerable<string> paths,
        bool? moveWhenMapped = null,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default,
        IProgress<FileService.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? preferredManualIndex = null,
        bool activateManualSortOnSuccess = false,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return [];
        }

        EnsureFolderBackedConfig();
        MappedFolderPath = Config.MappedFolderPath;
        if (string.IsNullOrWhiteSpace(CurrentFolderPath))
        {
            _mappedFolderTraversalPath = ResolveMappedFolderTraversalPath();
            SetCurrentFolderPath(_mappedFolderTraversalPath);
        }
        else
        {
            await RefreshMappedRootTraversalPathAsync(cancellationToken);
        }

        string destinationFolderPath = CurrentFolderPath!;
        bool shouldMove = moveWhenMapped ?? ShouldMoveManagedItems(
            normalizedPaths,
            destinationFolderPath);
        OrganizationHistoryEntry historyEntry;
        try
        {
            historyEntry = await _organizerService.OrganizeDropAsync(
                Config,
                Name,
                normalizedPaths,
                shouldMove,
                useShellProgress,
                ownerWindowHandle,
                progress,
                cancellationToken,
                destinationFolderPath);
        }
        catch (Exception ex) when (
            ex is FileService.IFileTransferWithCompletedResults partial)
        {
            await ApplyImportedTransferResultsAsync(
                partial.CompletedResults,
                shouldMove,
                destinationFolderPath,
                preferredManualIndex,
                activateManualSortOnSuccess,
                preferredStackAnchor);
            throw;
        }

        await ApplyImportedTransferResultsAsync(
            historyEntry.Items.Select(item => new FileService.FileTransferResult(
                item.SourcePath,
                item.DestinationPath)),
            shouldMove,
            destinationFolderPath,
            preferredManualIndex,
            activateManualSortOnSuccess,
            preferredStackAnchor);

        return historyEntry.Items
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ApplyImportedTransferResultsAsync(
        IEnumerable<FileService.FileTransferResult> results,
        bool shouldMove,
        string destinationFolderPath,
        int? preferredManualIndex = null,
        bool activateManualSortOnSuccess = false,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        FileService.FileTransferResult[] materialized = results
            .Where(result =>
                !string.IsNullOrWhiteSpace(result.SourcePath) &&
                !string.IsNullOrWhiteSpace(result.DestinationPath))
            .GroupBy(
                result => result.SourcePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        if (shouldMove)
        {
            foreach (string sourcePath in materialized.Select(
                         result => result.SourcePath))
            {
                if (Path.GetDirectoryName(sourcePath)?.Equals(
                        destinationFolderPath,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    RemoveItemByPath(sourcePath);
                }
            }
        }

        WidgetSortMode? originalSortMode = null;
        bool originalSortDescending = Config.SortDescending;
        if (activateManualSortOnSuccess &&
            (preferredManualIndex.HasValue ||
             preferredStackAnchor.HasValue) &&
            Config.SortMode != WidgetSortMode.Manual)
        {
            originalSortMode = Config.SortMode;
            Config.SortMode = WidgetSortMode.Manual;
            Config.SortDescending = false;
            NormalizeSortOrder();
            OnPropertyChanged(nameof(SortModeLabel));
        }

        bool insertedAny = false;
        var importedDestinationPaths = new List<string>();
        int nextManualIndex = preferredManualIndex ?? -1;
        foreach (string destinationPath in materialized.Select(
                     result => result.DestinationPath))
        {
            if (!File.Exists(destinationPath) &&
                !Directory.Exists(destinationPath))
            {
                continue;
            }

            RecordFileAddedAt(destinationPath, DateTimeOffset.Now);
            bool inserted = await UpsertFolderItemAsync(
                destinationPath,
                nextManualIndex >= 0 ? nextManualIndex : null);
            if (inserted && nextManualIndex >= 0)
            {
                nextManualIndex++;
            }

            insertedAny |= inserted;
            if (inserted)
            {
                importedDestinationPaths.Add(destinationPath);
            }
        }

        if (insertedAny &&
            preferredStackAnchor is { } stackAnchor)
        {
            ApplyImportedStackInsertion(
                importedDestinationPaths,
                stackAnchor);
        }

        if (originalSortMode.HasValue)
        {
            if (insertedAny)
            {
                // UpsertFolderItemAsync persists a manual snapshot after the
                // first successful result. Keep the explicit mode/order
                // write here as a final idempotent commit for an empty or
                // already-present destination path batch.
                PersistManualOrder();
            }
            else
            {
                Config.SortMode = originalSortMode.Value;
                Config.SortDescending = originalSortDescending;
                SortItems();
                OnPropertyChanged(nameof(SortModeLabel));
            }
        }
    }

    /// <summary>
    /// Applies a previously created destination batch (for example shortcut
    /// files) at the external-drop insertion point. The automatic sort mode is
    /// committed only when at least one destination can be represented in the
    /// widget; an ACL/provider race therefore cannot leave a phantom Manual
    /// mode behind.
    /// </summary>
    internal async Task<int> ApplyManualInsertionAsync(
        IEnumerable<string> destinationPaths,
        int preferredManualIndex,
        bool activateManualSortOnSuccess,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        string[] paths = destinationPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return 0;
        }

        WidgetSortMode? originalSortMode = null;
        bool originalSortDescending = Config.SortDescending;
        if (activateManualSortOnSuccess &&
            Config.SortMode != WidgetSortMode.Manual)
        {
            originalSortMode = Config.SortMode;
            Config.SortMode = WidgetSortMode.Manual;
            Config.SortDescending = false;
            NormalizeSortOrder();
            OnPropertyChanged(nameof(SortModeLabel));
        }

        int insertedCount = 0;
        var importedDestinationPaths = new List<string>();
        int nextManualIndex = Math.Max(0, preferredManualIndex);
        foreach (string path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            RecordFileAddedAt(path, DateTimeOffset.Now);
            if (await UpsertFolderItemAsync(path, nextManualIndex))
            {
                insertedCount++;
                nextManualIndex++;
                importedDestinationPaths.Add(path);
            }
        }

        if (insertedCount > 0 &&
            preferredStackAnchor is { } stackAnchor)
        {
            ApplyImportedStackInsertion(
                importedDestinationPaths,
                stackAnchor);
        }

        if (originalSortMode.HasValue)
        {
            if (insertedCount > 0)
            {
                PersistManualOrder();
            }
            else
            {
                Config.SortMode = originalSortMode.Value;
                Config.SortDescending = originalSortDescending;
                SortItems();
                OnPropertyChanged(nameof(SortModeLabel));
            }
        }

        return insertedCount;
    }

    internal async Task<IReadOnlyList<string>> GetConfirmedMissingPathsAsync(
        IEnumerable<string> paths)
    {
        var confirmedMissing = new List<string>();
        foreach (IGrouping<string, string> group in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(path => !string.IsNullOrWhiteSpace(Path.GetDirectoryName(path)))
                     .GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase))
        {
            FolderPathSnapshot snapshot =
                await FileService.CaptureDirectChildSnapshotAsync(group.Key);
            if (!FolderSnapshotStatusPolicy.IsSuccessful(snapshot.Status))
            {
                continue;
            }

            confirmedMissing.AddRange(group.Where(path =>
                FileService.ClassifyDirectChild(snapshot, path) ==
                FolderEntryRefreshStatus.NotFound));
        }

        return confirmedMissing;
    }

    /// <summary>
    /// Toggle between icon and list views.
    /// </summary>
    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewMode.Icon ? ViewMode.List : ViewMode.Icon;
        Config.ViewMode = ViewMode;
        _settingsService.SaveDebounced();
    }

    /// <summary>
    /// Open an item using the host window as the owner of any Shell UI.
    /// </summary>
    public FileService.OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)
    {
        var result = FileService.OpenItem(item, ownerHwnd);
        if (result == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(item.Path);
        }

        return result;
    }

    /// <summary>
    /// Opens an item on the bounded Shell worker and applies any collection
    /// change back on the caller's context.
    /// </summary>
    public async Task<FileService.OpenItemResult> OpenItemAsync(
        WidgetItem item,
        IntPtr ownerHwnd,
        CancellationToken cancellationToken = default)
    {
        string itemPath = item.Path;
        var result = await FileService.OpenItemAsync(
            item,
            ownerHwnd,
            cancellationToken);
        if (result == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(itemPath);
        }

        return result;
    }

    /// <summary>
    /// Reveal an item in Explorer.
    /// </summary>
    [RelayCommand]
    public void ShowInExplorer(WidgetItem item)
    {
        FileService.ShowInExplorer(item);
    }

    public async Task<int> MoveItemBackToDesktopAsync(
        WidgetItem item,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemBackToDesktopAsync(
            Config,
            Name,
            item,
            useShellProgress,
            ownerWindowHandle);
        if (historyEntry.Items.Any(entry => string.Equals(entry.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveItemByPath(item.Path);
            RemoveStackMemberOverridePaths([item.Path]);
            return 1;
        }

        return 0;
    }

    public async Task<int> MoveItemsBackToDesktopAsync(
        IEnumerable<WidgetItem> items,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemsBackToDesktopAsync(
            Config,
            Name,
            targets.Select(item => item.Path),
            useShellProgress,
            ownerWindowHandle);

        var movedSourcePaths = historyEntry.Items
            .Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets.Where(item => movedSourcePaths.Contains(item.Path)))
        {
            RemoveItemByPath(item.Path);
        }

        RemoveStackMemberOverridePaths(movedSourcePaths);

        return movedSourcePaths.Count;
    }

    public async Task RefreshFromConfigAsync()
    {
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshFromConfig",
            $"id={Config.Id} name={Name}");

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        EnsureFolderBackedConfig();

        MappedFolderPath = Config.MappedFolderPath;
        SetCurrentFolderPath(ResolveCurrentFolderForMappedRoot());
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));
        ApplyStackSettings();

        await ConfigureFolderWatchersAsync(CurrentFolderPath);
        await ReloadFolderContentsAsync(
            CurrentFolderPath!,
            clearIconCacheBeforeHydration: true);
        UpdateDependentProperties();
    }

    /// <summary>
    /// Lightweight folder refresh that only reloads the contents without
    /// restarting the folder watcher.  Use this when the watcher is already
    /// running and you just need to re-read the current disk state — e.g.
    /// after a drag-out operation where the Shell may still be moving files.
    /// Uses the same semaphore as <see cref="OnFolderChanged"/> to avoid
    /// concurrent <see cref="LoadFolderContentsAsync"/> calls.
    /// </summary>
    public async Task RefreshFolderContentsAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(CurrentFolderPath))
        {
            return;
        }

        string? refreshPath = await RefreshMappedRootTraversalPathAsync();
        if (string.IsNullOrWhiteSpace(refreshPath))
        {
            return;
        }

        await ReloadFolderContentsAsync(refreshPath);
        if (!_isDisposed)
        {
            UpdateDependentProperties();
        }
    }

    private async Task<bool> ReloadFolderContentsAsync(
        string expectedFolderPath,
        bool clearIconCacheBeforeHydration = false,
        CancellationToken cancellationToken = default,
        Action? beforeItemsReplaced = null,
        bool allowFolderPathTransition = false)
    {
        if (_isDisposed)
        {
            return false;
        }

        await _folderRefreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed ||
                string.IsNullOrEmpty(CurrentFolderPath) ||
                (!allowFolderPathTransition && !string.Equals(
                    Path.GetFullPath(CurrentFolderPath),
                    Path.GetFullPath(expectedFolderPath),
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return await LoadFolderContentsAsync(
                expectedFolderPath,
                clearIconCacheBeforeHydration,
                cancellationToken,
                beforeItemsReplaced);
        }
        finally
        {
            _folderRefreshGate.Release();
        }
    }

    public async Task UpdateMappedFolderPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = Path.GetFullPath(folderPath);
        if (App.Current?.WidgetManager is { } pathWidgetManager)
        {
            pathWidgetManager.EnsureFileWidgetPathAvailable(
                normalizedPath,
                Config.Id,
                candidateFollowsDefaultStoragePath: false);
        }
        else
        {
            WidgetConfig? conflict = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !string.Equals(widget.Id, Config.Id, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                WidgetManager.IsFileWidgetPathConflict(
                    normalizedPath,
                    candidateFollowsDefaultStoragePath: false,
                    widget));
            string managedStorageRoot =
                SettingsService.NormalizeManagedStorageRootPath(
                    _settingsService.Settings.DefaultManagedStorageRootPath);
            if (conflict is not null ||
                FileService.PathsOverlap(normalizedPath, managedStorageRoot))
            {
                throw new InvalidOperationException(_localizationService.Format(
                    "Widget.Error.FileWidgetPathConflict",
                    conflict?.Name ??
                    _localizationService.T("WidgetTitleIcon.Label.ManagedStorage")));
            }
        }

        if (!FileService.TryResolveExistingPathForTraversal(
                normalizedPath,
                out string traversalPath))
        {
            Directory.CreateDirectory(normalizedPath);
            traversalPath = FileService.TryResolveExistingPathForTraversal(
                normalizedPath,
                out string createdTraversalPath)
                ? createdTraversalPath
                : normalizedPath;
        }

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        Config.FollowsDefaultStoragePath = false;
        Config.ManagedFolderName = null;
        Config.MappedFolderPath = normalizedPath;
        Config.Items.Clear();
        ResetAddedAtTracking();
        MappedFolderPath = normalizedPath;
        _mappedFolderTraversalPath = traversalPath;
        SetCurrentFolderPath(traversalPath);
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

        if (App.Current?.WidgetManager is { } widgetManager)
        {
            widgetManager.SyncMappedWidgetShortcut(Config.Id);
        }

        _settingsService.UpdateWidget(Config);
        await ConfigureFolderWatchersAsync(traversalPath);
        await ReloadFolderContentsAsync(traversalPath);
        UpdateDependentProperties();
    }

    public Task HandleItemsMovedOutAsync(IEnumerable<string> sourcePaths)
    {
        var normalizedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var path in normalizedPaths)
        {
            RemoveItemByPath(path);
        }

        RemoveStackMemberOverridePaths(normalizedPaths);

        return Task.CompletedTask;
    }

    public async Task RenameItemAsync(WidgetItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string sanitizedName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        string sourcePath = Path.GetFullPath(item.Path);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.FolderUnknown"));
        }

        string destinationName = FileService.ResolveRenameDestination(
            Path.GetFileName(sourcePath),
            sanitizedName,
            item.IsFolder,
            ShortcutHelper.IsShortcutPath(sourcePath),
            _showFileExtensions,
            out bool requiresExtensionChangeConfirmation);
        string destinationPath = Path.Combine(parentDirectory, destinationName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return;
        }

        // Ask before any other validation so a declined extension change never
        // surfaces follow-up errors (e.g. a name collision with the new name).
        if (requiresExtensionChangeConfirmation &&
            ConfirmExtensionChangeHandler?.Invoke(sourcePath, destinationPath) != true)
        {
            App.Log(
                $"[Widget] Rename extension change declined " +
                $"source='{sourcePath}' destination='{destinationPath}'");
            return;
        }

        bool isCaseOnlyRename = FileService.IsCaseOnlyPathChange(
            sourcePath,
            destinationPath);
        if (!isCaseOnlyRename &&
            (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
        {
            throw new IOException(_localizationService.T("Widget.Validation.TargetExists"));
        }

        bool refreshGateHeld = false;
        bool restartWatchers = false;
        try
        {
            if (isCaseOnlyRename)
            {
                await _folderRefreshGate.WaitAsync();
                refreshGateHeld = true;
                _folderWatcher.Stop();
                _publicFolderWatcher.Stop();
                restartWatchers = true;
            }

            await _fileService.RenameEntryAsync(sourcePath, destinationPath);
            TransferFileAddedAt(sourcePath, destinationPath);
            var refreshedItem = await _fileService.CreateWidgetItemAsync(
                destinationPath,
                hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                showImageFilesAsIcons: _showImageFilesAsIcons,
                showFileExtensions: _showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                loadIcon: false,
                loadFolderItemCount: false,
                loadShortcutTarget: false);
            ApplyRuntimeItemData(item, refreshedItem);
            UpdateStackMemberOverridePath(
                sourcePath,
                destinationPath);
            StartItemHydration();

            int originalIndex = Items.IndexOf(item);
            if (originalIndex >= 0)
            {
                if (Config.SortMode != WidgetSortMode.Manual)
                {
                    Items.RemoveAt(originalIndex);
                    Items.Insert(GetSortedInsertIndex(item), item);
                }

                NormalizeSortOrder();
                PersistManualOrderSnapshotIfChanged();
            }
        }
        finally
        {
            try
            {
                if (restartWatchers &&
                    !_isDisposed &&
                    !string.IsNullOrWhiteSpace(CurrentFolderPath))
                {
                    await ConfigureFolderWatchersAsync(CurrentFolderPath);
                }
            }
            finally
            {
                if (refreshGateHeld)
                {
                    _folderRefreshGate.Release();
                }
            }
        }
    }

    public async Task<FileDeleteBatchResult> DeleteItemsAsync(
        IEnumerable<WidgetItem> items,
        bool recycle = true,
        IntPtr ownerHandle = default)
    {
        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            return new FileDeleteBatchResult(0, []);
        }

        int deletedCount = 0;
        var failures = new List<FileDeleteFailure>();
        var successfulPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ownerHandle != IntPtr.Zero)
        {
            try
            {
                IReadOnlySet<string> deletedPaths =
                    await _fileService.DeleteEntriesWithShellAsync(
                        targets.Select(item => item.Path),
                        recycle,
                        ownerHandle);

                foreach (WidgetItem item in targets)
                {
                    string normalizedPath = Path.GetFullPath(item.Path);
                    if (!deletedPaths.Contains(normalizedPath))
                    {
                        continue;
                    }

                    Items.Remove(item);
                    RemoveFileAddedAt(item.Path);
                    successfulPaths.Add(normalizedPath);
                    deletedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A Shell failure can happen after a subset of a batch has
                // completed. Keep the model in sync with the filesystem and
                // report only entries that still exist as failures.
                foreach (WidgetItem item in targets)
                {
                    string normalizedPath = Path.GetFullPath(item.Path);
                    if (!File.Exists(normalizedPath) &&
                        !Directory.Exists(normalizedPath))
                    {
                        Items.Remove(item);
                        RemoveFileAddedAt(item.Path);
                        successfulPaths.Add(normalizedPath);
                        deletedCount++;
                        continue;
                    }

                    failures.Add(new FileDeleteFailure(
                        item.Path,
                        item.Name,
                        ex.Message));
                }
            }

            NormalizeSortOrder();
            RemoveStackMemberOverridePaths(
                targets
                    .Where(item => successfulPaths.Contains(Path.GetFullPath(item.Path)))
                    .Select(item => item.Path));
            return new FileDeleteBatchResult(deletedCount, failures);
        }

        foreach (var item in targets)
        {
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                Items.Remove(item);
                RemoveFileAddedAt(item.Path);
                successfulPaths.Add(Path.GetFullPath(item.Path));
                deletedCount++;
                continue;
            }

            try
            {
                bool deleted = await _fileService.DeleteEntryAsync(
                    item.Path,
                    recycle,
                    ownerHandle);
                if (!deleted)
                {
                    // The user answered "No" to the Shell confirmation; keep
                    // the item and do not count it as deleted or failed.
                    continue;
                }

                Items.Remove(item);
                RemoveFileAddedAt(item.Path);
                successfulPaths.Add(Path.GetFullPath(item.Path));
                deletedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new FileDeleteFailure(
                    item.Path,
                    item.Name,
                    ex.Message));
            }
        }

        NormalizeSortOrder();
        RemoveStackMemberOverridePaths(
            targets
                .Where(item => successfulPaths.Contains(Path.GetFullPath(item.Path)))
                .Select(item => item.Path));
        return new FileDeleteBatchResult(deletedCount, failures);
    }

    public void UpdateBounds(double x, double y, double width, double height, bool persist)
    {
        Config.X = x;
        Config.Y = y;
        Config.Width = width;
        Config.Height = height;

        if (persist)
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
    }

    /// <summary>
    /// Rename the widget.
    /// </summary>
    public async Task RenameAsync(string newName)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            Name = Config.Name;
            MappedFolderPath = Config.MappedFolderPath;
            SetCurrentFolderPath(ResolveCurrentFolderForMappedRoot());
            OnPropertyChanged(nameof(FollowsDefaultStoragePath));
            return;
        }

        Name = newName;
        Config.Name = newName;
        Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(Config);
    }

    public void SetPositionLocked(bool value)
    {
        if (IsPositionLocked == value)
        {
            return;
        }

        IsPositionLocked = value;
        Config.IsPositionLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    public void SetSizeLocked(bool value)
    {
        if (IsSizeLocked == value)
        {
            return;
        }

        IsSizeLocked = value;
        Config.IsSizeLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    [RelayCommand]
    public void TogglePositionLock()
    {
        SetPositionLocked(!IsPositionLocked);
    }

    [RelayCommand]
    public void ToggleSizeLock()
    {
        SetSizeLocked(!IsSizeLocked);
    }
}
