using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// 播放轻量的 Windows 系统提醒音，不引入媒体资源或常驻播放器。
/// </summary>
internal static class PomodoroCompletionSoundService
{
    private const uint SoundAsync = 0x0001;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundAlias = 0x00010000;
    private const uint SoundSystem = 0x00200000;
    private const uint NotificationFlags =
        SoundAsync | SoundNoDefault | SoundAlias | SoundSystem;

    private const string NotificationSound = "SystemNotification";
    private const string FallbackSound = "SystemExclamation";

    public static bool TryPlay()
    {
        try
        {
            return SoundNativeMethods.PlaySound(
                    NotificationSound,
                    nint.Zero,
                    NotificationFlags) ||
                SoundNativeMethods.PlaySound(
                    FallbackSound,
                    nint.Zero,
                    NotificationFlags);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException)
        {
            App.Log($"[PomodoroWidget] Failed to play completion sound: {ex.Message}");
            return false;
        }
    }
}
