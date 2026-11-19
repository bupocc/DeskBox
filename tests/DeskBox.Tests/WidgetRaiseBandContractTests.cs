namespace DeskBox.Tests;

public sealed class WidgetRaiseBandContractTests
{
    [Fact]
    public void GroupRaise_NeverVisitsTheTopmostBand()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string groupRaise = SliceMethod(
            layerService,
            "public static void BringGroupTemporarilyToFront",
            "public static bool ApplyPeerOrderHighestToLowest");

        // #375: the historical 2N+2 TOPMOST/NOTOPMOST pulse storm contested
        // the topmost band tail with resident topmost floaters (IME bars).
        // The replacement is one relative ordering into the normal band.
        Assert.Contains("ApplyWindowOrderHighestToLowest", groupRaise, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowTopMost", groupRaise, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearWindowTopMost", groupRaise, StringComparison.Ordinal);
        Assert.DoesNotContain("BringWindowTemporarilyToFront", groupRaise, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleWidgetTemporaryRaise_UsesNormalBandFrontWithoutPulse()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string hold = SliceMethod(
            layerService,
            "public static void HoldTemporaryTopMost",
            "/// <summary>\r\n    /// Raises one dynamically layered widget to the top of the normal band".ReplaceLineEndings("\r\n"));

        Assert.Contains("RaiseWindowToNormalBandFront(windowHandle, showWindow)", hold, StringComparison.Ordinal);
        Assert.DoesNotContain("BringWindowTemporarilyToFront", hold, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarPress_RaisesOnlyThePressedWidget()
    {
        string window = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));

        // Pressing a grid's title bar used to batch-raise every visible widget
        // above the applications behind them. Picking one grid must never move
        // the others, so the group raise stays out of the press path.
        Assert.DoesNotContain(
            "ActivateAllVisibleWidgetsFromTitle",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "BeginWindowDragCore(e, ContentWidgetShell.TitleBar)",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuxiliaryWindowFrontPath_IsUnchanged()
    {
        // Settings/search popups and raised-band guests keep their existing
        // topmost-band semantics; the normal-band rewrite is widget-only.
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string bring = SliceMethod(
            manager,
            "public void BringAuxiliaryWindowToFront",
            "public void ReleaseRaisedBandGuest");

        Assert.Contains("BringWindowTemporarilyToFront(windowHandle)", bring, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickRevealHoldTopMost_IsUnchanged()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string hold = SliceMethod(
            layerService,
            "public static void HoldGroupTopMostWithoutActivation",
            "public static void BringToFront");

        Assert.Contains("SetWindowTopMost(handle)", hold, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
