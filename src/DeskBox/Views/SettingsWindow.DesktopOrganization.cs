namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    public void RefreshDesktopOrganizationState()
    {
        DesktopOrganizationSettingsSection?.Refresh();
        ViewModel.RefreshManagedStorageState();
    }
}
