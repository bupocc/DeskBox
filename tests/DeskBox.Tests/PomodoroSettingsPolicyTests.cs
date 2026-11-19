using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PomodoroSettingsPolicyTests
{
    [Fact]
    public void Defaults_UseFourRoundsAndFiveFifteenBreakCadence()
    {
        var settings = new AppSettings();

        Assert.Equal(PomodoroSettingsPolicy.DefaultRoundCount, settings.PomodoroRoundCount);
        Assert.Equal(PomodoroSettingsPolicy.DefaultFocusMinutes, settings.PomodoroFocusMinutes);
        Assert.Equal(
            PomodoroSettingsPolicy.DefaultShortBreakMinutes,
            settings.PomodoroShortBreakMinutes);
        Assert.Equal(
            PomodoroSettingsPolicy.DefaultLongBreakMinutes,
            settings.PomodoroLongBreakMinutes);
        Assert.Null(settings.PomodoroBreakMinutes);
        Assert.False(PomodoroSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void Defaults_EnableCompletionSoundAndNotification()
    {
        var settings = new AppSettings();

        Assert.Equal(
            PomodoroSettingsPolicy.DefaultCompletionSoundEnabled,
            settings.PomodoroCompletionSoundEnabled);
        Assert.Equal(
            PomodoroSettingsPolicy.DefaultCompletionNotificationEnabled,
            settings.PomodoroCompletionNotificationEnabled);
        Assert.True(settings.PomodoroCompletionSoundEnabled);
        Assert.True(settings.PomodoroCompletionNotificationEnabled);
    }

    [Fact]
    public void Normalize_PreservesExplicitlyDisabledCompletionAlerts()
    {
        var settings = new AppSettings
        {
            PomodoroCompletionSoundEnabled = false,
            PomodoroCompletionNotificationEnabled = false
        };

        Assert.False(PomodoroSettingsPolicy.Normalize(settings));

        Assert.False(settings.PomodoroCompletionSoundEnabled);
        Assert.False(settings.PomodoroCompletionNotificationEnabled);

        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        AppSettings restored =
            JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

        Assert.False(restored.PomodoroCompletionSoundEnabled);
        Assert.False(restored.PomodoroCompletionNotificationEnabled);
    }

    [Fact]
    public void ApplyDefaultPreferences_ReenablesCompletionAlerts()
    {
        var settings = new AppSettings
        {
            PomodoroCompletionSoundEnabled = false,
            PomodoroCompletionNotificationEnabled = false
        };

        SettingsService.ApplyDefaultPreferences(settings);

        Assert.True(settings.PomodoroCompletionSoundEnabled);
        Assert.True(settings.PomodoroCompletionNotificationEnabled);
    }

    [Fact]
    public void LegacyJsonWithoutCompletionAlertSettings_UsesEnabledDefaults()
    {
        AppSettings settings =
            JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.AppSettings)!;

        Assert.True(settings.PomodoroCompletionSoundEnabled);
        Assert.True(settings.PomodoroCompletionNotificationEnabled);
    }

    [Fact]
    public void Normalize_ClampsEveryNumericSettingToItsSupportedRange()
    {
        var settings = new AppSettings
        {
            PomodoroRoundCount = 99,
            PomodoroFocusMinutes = 0,
            PomodoroShortBreakMinutes = -10,
            PomodoroLongBreakMinutes = 999
        };

        Assert.True(PomodoroSettingsPolicy.Normalize(settings));

        Assert.Equal(PomodoroSettingsPolicy.MaxRoundCount, settings.PomodoroRoundCount);
        Assert.Equal(PomodoroSettingsPolicy.MinFocusMinutes, settings.PomodoroFocusMinutes);
        Assert.Equal(
            PomodoroSettingsPolicy.MinShortBreakMinutes,
            settings.PomodoroShortBreakMinutes);
        Assert.Equal(
            PomodoroSettingsPolicy.MaxLongBreakMinutes,
            settings.PomodoroLongBreakMinutes);
    }

    [Fact]
    public void LegacyBreakMinutes_MigrateOnceToShortBreakWithoutOverwritingLongBreak()
    {
        var settings = new AppSettings
        {
            PomodoroShortBreakMinutes = 5,
            PomodoroLongBreakMinutes = 20,
            PomodoroBreakMinutes = 9
        };

        Assert.True(PomodoroSettingsPolicy.Normalize(settings));

        Assert.Equal(9, settings.PomodoroShortBreakMinutes);
        Assert.Equal(20, settings.PomodoroLongBreakMinutes);
        Assert.Null(settings.PomodoroBreakMinutes);
        Assert.False(PomodoroSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void LegacyJsonKey_IsConsumedAndNotWrittenBackAfterMigration()
    {
        AppSettings settings = JsonSerializer.Deserialize(
            """
            {
              "pomodoroBreakMinutes": 11
            }
            """,
            SettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(11, settings.PomodoroBreakMinutes);
        Assert.True(PomodoroSettingsPolicy.Normalize(settings));

        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        Assert.Equal(11, settings.PomodoroShortBreakMinutes);
        Assert.DoesNotContain("\"pomodoroBreakMinutes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pomodoroShortBreakMinutes\": 11", json, StringComparison.Ordinal);
    }
}
