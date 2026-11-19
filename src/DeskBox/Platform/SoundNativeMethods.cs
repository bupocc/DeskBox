using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// winmm entry points for system sound playback (the pomodoro completion
/// cue). Kept in the Platform domain so interop never leaks into Services.
/// </summary>
internal static partial class SoundNativeMethods
{
    [LibraryImport(
        "winmm.dll",
        EntryPoint = "PlaySoundW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PlaySound(
        string soundName,
        nint moduleHandle,
        uint flags);
}
