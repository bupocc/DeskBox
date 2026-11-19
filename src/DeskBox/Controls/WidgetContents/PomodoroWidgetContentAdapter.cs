using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>将番茄钟界面接入统一格子内容生命周期。</summary>
public sealed class PomodoroWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent
{
    public PomodoroWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Func<PomodoroWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new PomodoroWidgetViewModel(
                config,
                localizationService,
                settingsService),
            viewFactory)
    {
    }

    private PomodoroWidgetContentAdapter(
        WidgetConfig config,
        PomodoroWidgetViewModel viewModel,
        Func<PomodoroWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new PomodoroWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Pomodoro)
        {
            throw new ArgumentException(
                "Pomodoro content requires a Pomodoro widget config.",
                nameof(config));
        }

        ViewModel = viewModel;
    }

    public PomodoroWidgetViewModel ViewModel { get; }

    public override Task InitializeAsync() => ViewModel.InitializeAsync();

    public override Task RefreshAsync() => ViewModel.RefreshAsync();

    public override void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
        if (MaterializedView is PomodoroWidgetContent content)
        {
            content.ApplyAppearance();
        }
    }

    public override void OnActivated() => ViewModel.OnActivated();

    public override void OnDeactivated() => ViewModel.OnDeactivated();

    public override void OnWindowVisibilityChanged(bool visible) =>
        ViewModel.OnWindowVisibilityChanged(visible);

    public override void OnWindowRevealCompleted() =>
        ViewModel.OnWindowRevealCompleted();

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (MaterializedView is PomodoroWidgetContent content)
        {
            content.BeginResponsiveLayoutTransition(
                targetContentWidth,
                targetContentHeight,
                isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        if (MaterializedView is PomodoroWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is PomodoroWidgetContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    protected override void Dispose(bool disposing)
    {
        (MaterializedView as IDisposable)?.Dispose();

        // Dispose the ViewModel first (stops the timer, detaches service events).
        ViewModel.Dispose();
    }
}
