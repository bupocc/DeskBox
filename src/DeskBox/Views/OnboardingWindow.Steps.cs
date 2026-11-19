using System.Numerics;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private const string StatusInfoGlyph = "\uE946";
    private const string StatusCompleteGlyph = "\uE73E";
    private const string StatusHiddenGlyph = "\uE890";
    private const string StatusVisibleGlyph = "\uE8A7";

    private int _summonToggleCount;

    // ════════════════════════════════════════════════════════════
    //  Step 2: destination card — shows the real managed root;
    //  changes happen in Settings (AppearanceDetail hosts the
    //  managed-storage cards, including the verified migration)
    // ════════════════════════════════════════════════════════════

    private void SetupDropStep()
    {
        // Slice reads (never the AppSettings facade passthroughs — the
        // facade-access contract test pins this file's budget).
        string root = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        string subfolder = _localizationService.T("Widget.ManagedFolderBaseName");
        string fullPath = System.IO.Path.Combine(root, subfolder);
        Step2PathValue.Text = fullPath;
        ToolTipService.SetToolTip(Step2PathValue, fullPath);
        string rootTail = System.IO.Path.GetFileName(root.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        ScenePathChipText.Text = string.IsNullOrWhiteSpace(rootTail)
            ? fullPath
            : $"{rootTail} \\ {subfolder}";
    }

    private void Step2PathChange_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettings("AppearanceDetail");
    }

    // ════════════════════════════════════════════════════════════
    //  Step 3: summon / dismiss (live, wired to the real toggle path)
    // ════════════════════════════════════════════════════════════

    private void SetupSummonStep()
    {
        string hotkeyText = GlobalHotkeyService.FormatActivation(
            GlobalHotkeyService.NormalizeActivation(
                _settingsService.Settings.GlobalHotkeyActivationKind,
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);
        Step3BodyText.Text = _localizationService.Format(
            "Onboarding.Step3.Body",
            hotkeyText);
        SceneKeycapText.Text = hotkeyText;
        RefreshSummonStatus();
    }

    private void RefreshSummonStatus()
    {
        if (Step3StatusText is null)
        {
            return;
        }

        bool hasVisible = App.Current.HasVisibleWidgetsForOnboarding;
        string key;
        string glyph;
        if (_hasCompletedSummonPractice)
        {
            key = "Onboarding.Step3.StatusDone";
            glyph = StatusCompleteGlyph;
        }
        else if (_hasToggledDuringSummonStep)
        {
            key = hasVisible
                ? "Onboarding.Step3.StatusShown"
                : "Onboarding.Step3.StatusHidden";
            glyph = hasVisible ? StatusVisibleGlyph : StatusHiddenGlyph;
        }
        else
        {
            key = "Onboarding.Step3.StatusReady";
            glyph = StatusInfoGlyph;
        }

        Step3StatusIcon.Glyph = glyph;
        Step3StatusText.Text = _localizationService.T(key);
    }

    private async void Step3Try_Click(object sender, RoutedEventArgs e)
    {
        Step3TryButton.IsEnabled = false;
        try
        {
            await App.Current.ToggleWidgetsForOnboardingAsync();
        }
        finally
        {
            Step3TryButton.IsEnabled = true;
        }
    }

    private void OnOnboardingWidgetsVisibilityChanged(bool hasVisibleWidgets)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _stepIndex != 2)
            {
                return;
            }

            _hasToggledDuringSummonStep = true;
            _summonToggleCount++;
            if (_summonToggleCount >= 2)
            {
                _hasCompletedSummonPractice = true;
            }

            RefreshSummonStatus();

            // The keycap answers the real toggle with a physical press-pop.
            var keycapVisual = GetVisual(SceneKeycap);
            keycapVisual.StopAnimation("Scale");
            var pop = keycapVisual.Compositor.CreateVector3KeyFrameAnimation();
            pop.Duration = TimeSpan.FromMilliseconds(340);
            pop.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
            pop.InsertKeyFrame(0.35f, new Vector3(0.86f, 0.86f, 1f), EaseOut());
            pop.InsertKeyFrame(1f, Vector3.One, EaseOut());
            keycapVisual.StartAnimation("Scale", pop);
        });
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: the tray menu, opened for real
    // ════════════════════════════════════════════════════════════

    private void Step4OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowTrayContextMenuForOnboarding();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: optional feature widgets (persist immediately)
    // ════════════════════════════════════════════════════════════

    private void SetupFeatureStep()
    {
        if (_hasInitializedFeatureToggles)
        {
            return;
        }

        SynchronizeFeatureTogglesFromSettings();
    }

    private void SetFeatureTogglesEnabled(bool enabled)
    {
        Step5TodoToggle.IsEnabled = enabled;
        Step5QuickCaptureToggle.IsEnabled = enabled;
        Step5SearchToggle.IsEnabled = enabled;
        Step5WeatherToggle.IsEnabled = enabled;
        Step5MusicToggle.IsEnabled = enabled;
        Step5GlanceToggle.IsEnabled = enabled;
        Step5PomodoroToggle.IsEnabled = enabled;
    }

    private void Step5FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_hasInitializedFeatureToggles ||
            _isSynchronizingFeatureToggles ||
            sender is not ToggleSwitch { Tag: string kindName } toggle ||
            !Enum.TryParse(kindName, ignoreCase: false, out WidgetKind kind) ||
            !FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        _featureWidgetSelectionUpdateTask = PersistFeatureWidgetSelectionAfterAsync(
            _featureWidgetSelectionUpdateTask,
            kind,
            toggle.IsOn);
    }

    private async Task PersistFeatureWidgetSelectionAfterAsync(
        Task previousUpdate,
        WidgetKind kind,
        bool enabled)
    {
        try
        {
            await previousUpdate;

            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(
                    kind,
                    enabled,
                    reveal: enabled);
                return;
            }

            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to persist feature selection kind={kind} enabled={enabled}: {ex}");
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
    }

    private void OnFeatureWidgetSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnFeatureWidgetSettingsChanged);
            return;
        }

        if (_hasInitializedFeatureToggles)
        {
            SynchronizeFeatureTogglesFromSettings();
        }
    }

    private void SynchronizeFeatureTogglesFromSettings()
    {
        _isSynchronizingFeatureToggles = true;
        try
        {
            Step5TodoToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Todo);
            Step5QuickCaptureToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.QuickCapture);
            Step5SearchToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Search);
            Step5WeatherToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Weather);
            Step5MusicToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Music);
            Step5GlanceToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Glance);
            Step5PomodoroToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Pomodoro);
            _hasInitializedFeatureToggles = true;
        }
        finally
        {
            _isSynchronizingFeatureToggles = false;
        }
    }
}
