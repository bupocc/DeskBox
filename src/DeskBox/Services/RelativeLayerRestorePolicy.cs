namespace DeskBox.Services;

internal enum RelativeLayerRestoreDisposition
{
    DesktopBottom,
    PreservePeerOrder,
    BehindForeground,
}

internal static class RelativeLayerRestorePolicy
{
    public static bool ShouldAttachToDesktop(
        bool usesDesktopPinnedMode,
        bool keepVisibleOnShowDesktop)
    {
        return usesDesktopPinnedMode || keepVisibleOnShowDesktop;
    }

    public static RelativeLayerRestoreDisposition Decide(
        bool hasForeground,
        bool foregroundIsDesktopShell,
        bool foregroundIsSelf,
        bool foregroundIsDeskBox)
    {
        if (!hasForeground || foregroundIsDesktopShell)
        {
            return RelativeLayerRestoreDisposition.DesktopBottom;
        }

        if (foregroundIsSelf)
        {
            return RelativeLayerRestoreDisposition.PreservePeerOrder;
        }

        if (foregroundIsDeskBox)
        {
            // Another DeskBox widget now owns the foreground: this one is no
            // longer the widget the user is working with, so it must not stay
            // suspended above foreign applications. Only the interacted widget
            // keeps the raised band; everyone else returns to the desktop.
            return RelativeLayerRestoreDisposition.DesktopBottom;
        }

        return RelativeLayerRestoreDisposition.BehindForeground;
    }
}
