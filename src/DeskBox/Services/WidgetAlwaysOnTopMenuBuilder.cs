using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Right-click "always on top" toggle for a single widget. This is a user-level
/// pin (it survives focus changes and restarts), not the temporary raise the
/// z-order pipeline owns after a click.
/// </summary>
internal static class WidgetAlwaysOnTopMenuBuilder
{
    public static ToggleMenuFlyoutItem Create(
        LocalizationService localizationService,
        bool isAlwaysOnTop,
        Action<bool> setAlwaysOnTop)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.AlwaysOnTop"),
            Icon = new FontIcon { Glyph = "\uE840" },
            IsChecked = isAlwaysOnTop
        };
        item.Click += (_, _) => setAlwaysOnTop(item.IsChecked);
        return item;
    }
}
