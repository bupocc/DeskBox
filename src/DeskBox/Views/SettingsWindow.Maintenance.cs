using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void RefreshDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDragDropPermissionDiagnostic();
    }

    private async void ResyncRuntimeStateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResyncRuntimeStateAsync();
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            DeskBoxDiagnosticSnapshot snapshot = App.Current.CreateDiagnosticSnapshot();
            string archivePath = await App.Current.DiagnosticsBundleService.ExportAsync(
                folderPath,
                snapshot,
                DeskBoxDataPathService.Current.LogFilePath);
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Diagnostics.SuccessTitle"),
                _localizationService.Format("Settings.Diagnostics.SuccessBody", archivePath));
            Win32Helper.ShowInExplorer(archivePath);
        }
        catch (Exception ex)
        {
            App.Log($"[DiagnosticsBundle] Export failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Diagnostics.FailedTitle"),
                _localizationService.Format("Settings.Diagnostics.FailedBody", ex.Message));
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void RepairDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = ViewModel.RepairDragDropPermission();
        if (result.RequiresStartupSettings)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.AutoStart.Title"),
                PrimaryButtonText = _localizationService.T(
                    "Settings.AutoStart.OpenSystemSettings"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.T(
                        "Settings.AutoStart.WindowsDisabled"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await OpenStartupAppsSettingsAsync();
            }

            return;
        }

        if (result.NeedsRelaunch)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.DragDropPermission.RelaunchTitle"),
                PrimaryButtonText = _localizationService.T("Settings.DragDropPermission.RelaunchButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.T("Settings.DragDropPermission.RelaunchBody"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (DragDropPermissionService.TryRelaunchAsExplorerUser())
                {
                    App.Current.Exit();
                }
                else
                {
                    await ShowInfoDialogAsync(
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedTitle"),
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedBody"));
                }
            }

            return;
        }

        await ShowInfoDialogAsync(
            _localizationService.T(result.Success
                ? "Settings.DragDropPermission.RepairCompleteTitle"
                : "Settings.DragDropPermission.RepairFailedTitle"),
            result.Success
                ? _localizationService.Format("Settings.DragDropPermission.RepairCompleteBody", result.RepairedCount)
                : result.FailureMessage);
    }

    private async void OpenUacSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => Win32Helper.OpenFile("UserAccountControlSettings.exe"));
    }

    private async void ExportDataBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        ExportDataBackupButton.IsEnabled = false;
        try
        {
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string backupPath = await App.Current.DataBackupService.ExportBackupAsync(folderPath);
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.SuccessTitle"),
                _localizationService.Format("Settings.DataBackup.SuccessBody", backupPath));
            Win32Helper.ShowInExplorer(backupPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Manual export failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
        finally
        {
            ExportDataBackupButton.IsEnabled = true;
        }
    }

    private async void WebDavUploadButton_Click(object sender, RoutedEventArgs e)
    {
        await SyncWebDavAsync(upload: true, sender as FrameworkElement);
    }

    private void WebDavSettingTextChanged(object sender, TextChangedEventArgs e)
    {
        SaveWebDavSettingsFromView(sender as FrameworkElement);
    }

    private void WebDavPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && !string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            string username = FindDescendant<TextBox>(FindVisualRoot(passwordBox), x => Equals(x.Tag, "WebDav.Username"))?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(username)) WebDavBackupService.SavePassword(username, passwordBox.Password);
        }
    }

    private void SaveWebDavSettingsFromView(FrameworkElement? source)
    {
        DependencyObject? root = FindVisualRoot(source);
        TextBox? url = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Url"));
        TextBox? user = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Username"));
        TextBox? directory = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Directory"));
        if (url is null || user is null || directory is null) return;
        AppSettings settings = App.Current.SettingsService.Settings;
        settings.WebDavBackupUrl = url.Text.Trim();
        settings.WebDavBackupUsername = user.Text.Trim();
        settings.WebDavBackupRemoteDirectory = string.IsNullOrWhiteSpace(directory.Text) ? "DeskBox" : directory.Text.Trim();
        settings.WebDavBackupEnabled = Uri.TryCreate(settings.WebDavBackupUrl, UriKind.Absolute, out Uri? endpoint) &&
            (endpoint.Scheme is "http" or "https") &&
            !string.IsNullOrWhiteSpace(settings.WebDavBackupUsername);
        App.Current.SettingsService.SaveDebounced(notifySubscribers: false);
    }

    private async void WebDavTestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        DependencyObject? root = FindVisualRoot(sender as FrameworkElement);
        TextBox? url = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Url"));
        TextBox? user = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Username"));
        TextBox? dir = FindDescendant<TextBox>(root, x => Equals(x.Tag, "WebDav.Directory"));
        PasswordBox? pwd = FindDescendant<PasswordBox>(root, x => Equals(x.Tag, "WebDav.Password"));
        TextBlock? status = FindDescendant<TextBlock>(root, x => Equals(x.Tag, "WebDav.Status"));
        if (!Uri.TryCreate(url?.Text?.Trim(), UriKind.Absolute, out Uri? endpoint)) { if (status is not null) status.Text = "请输入有效的 WebDAV 地址。"; return; }
        var result = await App.Current.Services.GetRequiredService<WebDavBackupService>().TestConnectionAsync(endpoint, user?.Text?.Trim() ?? "", pwd?.Password ?? "", dir?.Text?.Trim() ?? "DeskBox");
        AppSettings settings = App.Current.SettingsService.Settings;
        settings.WebDavBackupEnabled = result.Succeeded && string.IsNullOrEmpty(result.ErrorMessage);
        settings.WebDavBackupUrl = endpoint.ToString();
        settings.WebDavBackupUsername = user?.Text?.Trim() ?? string.Empty;
        settings.WebDavBackupRemoteDirectory = dir?.Text?.Trim() ?? "DeskBox";
        await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
        if (status is not null) status.Text = result.Succeeded ? "WebDAV 连接成功。" : $"连接失败：{result.ErrorMessage}";
    }

    private async void WebDavDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        await RestoreWebDavVersionAsync(sender as FrameworkElement);
    }

    private async Task RestoreWebDavVersionAsync(FrameworkElement? source)
    {
        DependencyObject? root = FindVisualRoot(source);
        TextBox? urlBox = FindDescendant<TextBox>(root, box => Equals(box.Tag, "WebDav.Url"));
        TextBox? userBox = FindDescendant<TextBox>(root, box => Equals(box.Tag, "WebDav.Username"));
        TextBox? directoryBox = FindDescendant<TextBox>(root, box => Equals(box.Tag, "WebDav.Directory"));
        PasswordBox? passwordBox = FindDescendant<PasswordBox>(root, box => Equals(box.Tag, "WebDav.Password"));
        TextBlock? statusText = FindDescendant<TextBlock>(root, text => Equals(text.Tag, "WebDav.Status"));
        if (!Uri.TryCreate(urlBox?.Text?.Trim(), UriKind.Absolute, out Uri? endpoint))
        {
            if (statusText is not null) statusText.Text = "请输入有效的 WebDAV HTTP(S) 地址。";
            return;
        }

        string username = userBox?.Text?.Trim() ?? string.Empty;
        string password = passwordBox?.Password ?? WebDavBackupService.TryGetPassword(username) ?? string.Empty;
        string remoteDirectory = directoryBox?.Text?.Trim() ?? "DeskBox";
        try
        {
            IReadOnlyList<WebDavBackupVersion> versions = await App.Current.Services
                .GetRequiredService<WebDavBackupService>()
                .ListRemoteVersionsAsync(endpoint, username, password, remoteDirectory);
            if (versions.Count == 0)
            {
                if (statusText is not null) statusText.Text = "远端没有可恢复的备份版本。";
                return;
            }

            var choices = versions.Select(version =>
                $"{version.BackupTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  版本 {version.VersionId}  ·  {FormatBytes(version.SizeBytes)}")
                .ToArray();
            var selector = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
                Width = 500,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = "请选择远端备份版本（按时间倒序，最多显示 10 个）：", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(selector);
            content.Children.Add(new TextBlock { Text = "恢复前会先创建一份本地恢复前快照。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "从 WebDAV 恢复",
                Content = content,
                PrimaryButtonText = "恢复所选版本",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || selector.SelectedIndex < 0)
            {
                return;
            }

            WebDavBackupVersion selected = versions[selector.SelectedIndex];
            string archivePath = Path.Combine(Path.GetTempPath(), $"DeskBox-WebDAV-{selected.VersionId}.zip");
            await App.Current.Services.GetRequiredService<WebDavBackupService>().DownloadAsync(
                WebDavBackupService.BuildRemoteArchiveUri(endpoint, remoteDirectory, selected.VersionId),
                username,
                password,
                archivePath);
            await App.Current.DataBackupService.CreateAutomaticSnapshotNowAsync();
            await RestoreDataBackupFromPathAsync(archivePath);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] WebDAV restore failed: {ex}");
            if (statusText is not null) statusText.Text = $"从 WebDAV 恢复失败：{ex.Message}";
        }
    }

    private async Task SyncWebDavAsync(bool upload, FrameworkElement? source)
    {
        DependencyObject? visualRoot = FindVisualRoot(source);
        TextBox? urlBox = FindDescendant<TextBox>(visualRoot, box => Equals(box.Tag, "WebDav.Url"));
        TextBox? userBox = FindDescendant<TextBox>(visualRoot, box => Equals(box.Tag, "WebDav.Username"));
        TextBox? directoryBox = FindDescendant<TextBox>(visualRoot, box => Equals(box.Tag, "WebDav.Directory"));
        PasswordBox? passwordBox = FindDescendant<PasswordBox>(visualRoot, box => Equals(box.Tag, "WebDav.Password"));
        TextBlock? statusText = FindDescendant<TextBlock>(visualRoot, text => Equals(text.Tag, "WebDav.Status"));
        if (!Uri.TryCreate(urlBox?.Text?.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            if (statusText is not null) statusText.Text = "请输入有效的 WebDAV HTTP(S) 地址。";
            return;
        }

        string user = userBox?.Text?.Trim() ?? string.Empty;
        string password = passwordBox?.Password ?? string.Empty;
        string remoteDirectory = directoryBox?.Text?.Trim() ?? "DeskBox";
        try
        {
            if (!string.IsNullOrEmpty(password)) WebDavBackupService.SavePassword(user, password);
            App.Current.SettingsService.Settings.WebDavBackupUrl = endpoint.ToString();
            App.Current.SettingsService.Settings.WebDavBackupUsername = user;
            App.Current.SettingsService.Settings.WebDavBackupRemoteDirectory = remoteDirectory;
            App.Current.SettingsService.Settings.WebDavBackupEnabled = true;
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string? archive = null;
            if (upload)
            {
                await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
                archive = await App.Current.DataBackupService.ExportBackupAsync(Path.GetTempPath());
            }
            if (archive is null)
            {
                if (statusText is not null) statusText.Text = "没有可上传的本地备份。";
                return;
            }
            WebDavSyncResult result = await App.Current.Services.GetRequiredService<WebDavBackupService>().SyncAsync(
                endpoint,
                user,
                password,
                archive,
                remoteDirectory,
                overwriteConflict: true);
            if (result.Conflict is { } conflict)
            {
                string message = $"本地版本：{conflict.Local.VersionId}（{conflict.Local.BackupTimeUtc.ToLocalTime():g}）\n" +
                                 $"远程版本：{conflict.Remote.VersionId}（{conflict.Remote.BackupTimeUtc.ToLocalTime():g}）\n\n请选择要保留的版本。";
                var dialog = new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = "WebDAV 备份冲突", Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "保留本地并覆盖远程", SecondaryButtonText = "恢复远程版本", CloseButtonText = "取消" };
                ContentDialogResult choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.Secondary && conflict.Remote.RemotePath is { } remotePath)
                {
                    string destination = Path.Combine(Path.GetTempPath(), $"DeskBox-WebDAV-{conflict.Remote.VersionId}.zip");
                    await App.Current.Services.GetRequiredService<WebDavBackupService>().DownloadAsync(new Uri(remotePath), user, password, destination);
                    await RestoreDataBackupFromPathAsync(destination);
                }
                else if (choice == ContentDialogResult.Primary)
                {
                    await App.Current.Services.GetRequiredService<WebDavBackupService>().SyncAsync(endpoint, user, password, archive, remoteDirectory, overwriteConflict: true, cancellationToken: CancellationToken.None);
                }
                return;
            }
            if (statusText is not null) statusText.Text = result.Succeeded ? $"已同步版本 {result.UploadedVersion?.VersionId}。" : $"同步失败：{result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            if (statusText is not null) statusText.Text = $"WebDAV 操作失败：{ex.Message}";
        }
        finally { }
    }

    private static DependencyObject? FindVisualRoot(DependencyObject? child)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            if (parent is null) return current;
            current = parent;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is null) return null;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed && predicate(typed)) return typed;
            T? nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        return bytes >= mb ? $"{bytes / (double)mb:0.##} MB" : bytes >= kb ? $"{bytes / (double)kb:0.##} KB" : $"{bytes} B";
    }

    private async void RestoreDataBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        Windows.Storage.StorageFile? backupFile;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, _hWnd);
            backupFile = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Restore picker failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.RestoreFailedTitle"),
                _localizationService.Format("Settings.DataBackup.RestoreFailedBody", ex.Message));
            return;
        }

        if (backupFile is null || string.IsNullOrWhiteSpace(backupFile.Path))
        {
            return;
        }

        await RestoreDataBackupFromPathAsync(backupFile.Path);
    }

    private async Task RestoreDataBackupFromPathAsync(string archivePath)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        RestoreDataBackupButton.IsEnabled = false;
        ExportDataBackupButton.IsEnabled = false;
        bool restartScheduled = false;
        try
        {
            DeskBoxRestorePreparation preparation = await App.Current.DataBackupService.PrepareRestoreAsync(
                archivePath);
            string integrityWarning = preparation.HasIntegrityManifest
                ? string.Empty
                : $"\n\n{_localizationService.T("Settings.DataBackup.LegacyIntegrityWarning")}";
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.DataBackup.RestoreConfirmTitle"),
                PrimaryButtonText = _localizationService.T("Settings.DataBackup.RestoreConfirmButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.DataBackup.RestoreConfirmBody",
                        preparation.BackupCreatedAtUtc.ToLocalTime().ToString("g"),
                        preparation.AppVersion,
                        preparation.FileCount,
                        ViewModel.FormatBytes(preparation.TotalUncompressedBytes),
                        integrityWarning),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
                return;
            }

            AppRelaunchScheduleResult relaunch = AppRelaunchService.ScheduleAfterCurrentProcessExit();
            if (!relaunch.Started)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.DataBackup.RestartFailedTitle"),
                    _localizationService.Format(
                        "Settings.DataBackup.RestartFailedBody",
                        relaunch.ErrorMessage ?? string.Empty));
                return;
            }

            restartScheduled = true;
            await App.Current.ShutdownForRestartAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Restore preparation failed: {ex}");
            if (!restartScheduled)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
            }
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.RestoreFailedTitle"),
                _localizationService.Format("Settings.DataBackup.RestoreFailedBody", ex.Message));
        }
        finally
        {
            if (!restartScheduled && !_isClosed)
            {
                RestoreDataBackupButton.IsEnabled = true;
                ExportDataBackupButton.IsEnabled = true;
            }
        }
    }

    private async void CreateBackupSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        CreateBackupSnapshotButton.IsEnabled = false;
        RefreshBackupSnapshotsButton.IsEnabled = false;
        try
        {
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string? snapshotPath = await App.Current.DataBackupService.CreateAutomaticSnapshotNowAsync();
            if (snapshotPath is null)
            {
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.DataBackup.FailedTitle"),
                    _localizationService.Format(
                        "Settings.DataBackup.FailedBody",
                        _localizationService.T("Settings.DataBackup.Snapshots.Empty")));
                return;
            }

            await RefreshBackupSnapshotInventoryAsync();
            Win32Helper.ShowInExplorer(snapshotPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Immediate snapshot failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
        finally
        {
            CreateBackupSnapshotButton.IsEnabled = true;
            RefreshBackupSnapshotsButton.IsEnabled = true;
        }
    }

    private async void ChangeAutomaticBackupDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        if (!App.Current.DataBackupService.IsValidCustomAutomaticBackupDirectory(
                folderPath,
                out string? rejectionReasonKey))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.AutomaticBackupDirectory.InvalidTitle"),
                _localizationService.T(rejectionReasonKey ?? "Settings.DataBackup.AutomaticBackupDirectory.InvalidPath"));
            return;
        }

        ViewModel.UpdateAutomaticBackupDirectory(folderPath);
        await RefreshBackupSnapshotInventoryAsync();
    }

    private async void ResetAutomaticBackupDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UpdateAutomaticBackupDirectory(string.Empty);
        await RefreshBackupSnapshotInventoryAsync();
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // With a custom folder configured, open it directly; otherwise keep the
        // pre-existing behavior of opening the recovery root that contains the
        // default "automatic" snapshot folder.
        AutomaticBackupDirectoryStatus status = App.Current.DataBackupService.GetAutomaticBackupDirectoryStatus();
        string directory = status.IsCustomDirectoryActive
            ? status.EffectiveDirectory
            : Path.GetDirectoryName(App.Current.DataBackupService.AutomaticSnapshotDirectory)
              ?? App.Current.DataBackupService.AutomaticSnapshotDirectory;
        Directory.CreateDirectory(directory);
        Win32Helper.ShowInExplorer(directory);
    }

    private async void RefreshBackupSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBackupSnapshotInventoryAsync();
    }

    private async Task RefreshBackupSnapshotInventoryAsync()
    {
        if (_isRefreshingBackupSnapshots || BackupSnapshotsList is null)
        {
            return;
        }

        _isRefreshingBackupSnapshots = true;
        RefreshBackupSnapshotsButton.IsEnabled = false;
        try
        {
            IReadOnlyList<DeskBoxBackupSnapshotInfo> snapshots =
                await App.Current.DataBackupService.GetSnapshotInventoryAsync();
            var rows = snapshots.Select(snapshot =>
            {
                string kind = snapshot.Kind == "pre-restore"
                    ? _localizationService.T("Settings.DataBackup.Snapshots.PreRestore")
                    : _localizationService.T("Settings.DataBackup.Snapshots.Automatic");
                string status = snapshot.IsReadable
                    ? _localizationService.T("Settings.DataBackup.Snapshots.Readable")
                    : _localizationService.T("Settings.DataBackup.Snapshots.Unreadable");
                string title = $"{kind} · {snapshot.CreatedAtUtc.ToLocalTime():g}";
                string details = $"{ViewModel.FormatBytes(snapshot.SizeBytes)} · {status}\n{Path.GetFileName(snapshot.Path)}";
                return new BackupSnapshotListItem(snapshot.Path, title, details, snapshot.IsReadable);
            }).ToArray();

            BackupSnapshotsList.ItemsSource = rows.Cast<object>().ToArray();
            BackupSnapshotSummaryText.Text = rows.Length == 0
                ? _localizationService.T("Settings.DataBackup.Snapshots.Empty")
                : _localizationService.Format(
                    "Settings.DataBackup.Snapshots.Summary",
                    rows.Length,
                    ViewModel.FormatBytes(snapshots.Sum(snapshot => snapshot.SizeBytes)),
                    snapshots[0].CreatedAtUtc.ToLocalTime().ToString("g"));
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Snapshot inventory failed: {ex}");
            BackupSnapshotsList.ItemsSource = null;
            BackupSnapshotSummaryText.Text = _localizationService.Format(
                "Settings.DataBackup.FailedBody",
                ex.Message);
        }
        finally
        {
            RefreshBackupSnapshotsButton.IsEnabled = true;
            _isRefreshingBackupSnapshots = false;
        }
    }

    private async void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BackupSnapshotListItem item } && item.CanRestore)
        {
            await RestoreDataBackupFromPathAsync(item.Path);
        }
    }

    private async void DeleteSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null || sender is not Button { DataContext: BackupSnapshotListItem item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.DataBackup.Snapshots.DeleteConfirmTitle"),
            PrimaryButtonText = _localizationService.T("Settings.DataBackup.Snapshots.Delete"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.DataBackup.Snapshots.DeleteConfirmBody",
                    item.Title),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await App.Current.DataBackupService.DeleteSnapshotAsync(item.Path);
            await RefreshBackupSnapshotInventoryAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Snapshot deletion failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
    }

    private async void CheckAttachmentHealthButton_Click(object sender, RoutedEventArgs e)
    {
        CheckAttachmentHealthButton.IsEnabled = false;
        AttachmentHealthSummaryText.Text = _localizationService.T("Settings.AttachmentHealth.Checking");
        try
        {
            DeskBoxAttachmentHealthReport report = await App.Current.AttachmentHealthService.ScanAsync();
            string key = report.UnreadableStoreCount > 0
                ? "Settings.AttachmentHealth.Partial"
                : report.IsHealthy
                    ? "Settings.AttachmentHealth.Healthy"
                    : "Settings.AttachmentHealth.Issues";
            AttachmentHealthSummaryText.Text = _localizationService.Format(
                key,
                report.ReferencedFileCount,
                report.MissingLinkedFiles.Count,
                report.MissingManagedFiles.Count,
                report.OrphanManagedFiles.Count,
                report.UnreadableStoreCount);
        }
        catch (Exception ex)
        {
            App.Log($"[AttachmentHealth] Scan failed: {ex}");
            AttachmentHealthSummaryText.Text = _localizationService.Format(
                "Settings.AttachmentHealth.Failed",
                ex.Message);
        }
        finally
        {
            CheckAttachmentHealthButton.IsEnabled = true;
        }
    }

    private async void RestoreDefaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Dialog.RestoreTitle"),
            PrimaryButtonText = _localizationService.T("Common.Restore"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.Dialog.RestoreBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RestoreDefaultPreferencesAsync();
    }

}
