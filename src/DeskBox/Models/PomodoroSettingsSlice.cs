using System.Text.Json.Serialization;
using DeskBox.Services;

namespace DeskBox.Models;

/// <summary>
/// Pomodoro timer preferences: phase durations, cycle round count, and the
/// completion alerts. Every value is clamped by
/// <see cref="PomodoroSettingsPolicy"/> on load and save, so the slice only
/// owns the persisted defaults.
/// </summary>
public sealed class PomodoroSettingsSlice
{
    // ─── Pomodoro Settings ─────────────────────────────────────────────
    /// <summary>Number of focus rounds per cycle. Clamped to 1..12.</summary>
    public int PomodoroRoundCount { get; set; } = PomodoroSettingsPolicy.DefaultRoundCount;

    /// <summary>Focus phase duration in minutes. Clamped to 1..120.</summary>
    public int PomodoroFocusMinutes { get; set; } = PomodoroSettingsPolicy.DefaultFocusMinutes;

    /// <summary>Short break duration in minutes. Clamped to 1..60.</summary>
    public int PomodoroShortBreakMinutes { get; set; } = PomodoroSettingsPolicy.DefaultShortBreakMinutes;

    /// <summary>Long break duration in minutes. Clamped to 1..60.</summary>
    public int PomodoroLongBreakMinutes { get; set; } = PomodoroSettingsPolicy.DefaultLongBreakMinutes;

    /// <summary>Whether a completion chime plays when a phase ends.</summary>
    public bool PomodoroCompletionSoundEnabled { get; set; } = PomodoroSettingsPolicy.DefaultCompletionSoundEnabled;

    /// <summary>Whether a system toast is raised when a phase ends.</summary>
    public bool PomodoroCompletionNotificationEnabled { get; set; } = PomodoroSettingsPolicy.DefaultCompletionNotificationEnabled;

    /// <summary>
    /// Legacy single break-length key (<c>pomodoroBreakMinutes</c>) written by
    /// the pre-merge pomodoro build, kept so those settings files still fold
    /// into <see cref="PomodoroShortBreakMinutes"/> instead of being ignored.
    /// Nullable and omitted when null so untouched profiles stay byte-stable;
    /// <see cref="PomodoroSettingsPolicy.Normalize"/> migrates it and clears it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PomodoroBreakMinutes { get; set; }
}
