using DeskBox.Helpers;
using System.Drawing;
using System.Reflection;

namespace DeskBox.Tests;

public class IconHelperTests
{
    [Theory]
    [InlineData("solution.sln", true)]
    [InlineData("solution.slnx", true)]
    [InlineData("solution.csproj", false)]
    [InlineData("solution.sln.bak", false)]
    public void SolutionFilePathDetection_IsLimitedToSolutionFormats(
        string path,
        bool expected)
    {
        Assert.Equal(expected, IconHelper.IsSolutionFilePath(path));
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MOV")]
    [InlineData(@"C:\media\clip.mkv")]
    [InlineData("clip.webm")]
    [InlineData("clip.m2ts")]
    public void IsVideoFile_RecognizesSupportedVideoExtensions(string path)
    {
        Assert.True(IconHelper.IsVideoFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.HEIC")]
    public void IsMediaFile_IncludesImages(string path)
    {
        Assert.True(IconHelper.IsImageFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("video.mp4.txt")]
    public void IsMediaFile_RejectsNonMediaExtensions(string path)
    {
        Assert.False(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HighResolutionShellItemPolicy_CoversOrdinaryItemsWithoutChangingShortcuts(
        bool isShortcutPath,
        bool expected)
    {
        Assert.Equal(
            expected,
            IconHelper.ShouldPreferHighResolutionShellItemIcon(isShortcutPath));
    }

    [Theory]
    [InlineData("game.url", true)]
    [InlineData("GAME.URL", true)]
    [InlineData("game.lnk", false)]
    [InlineData("game.url.txt", false)]
    [InlineData("", false)]
    public void InternetShortcutPathDetection_IsLimitedToUrlFiles(
        string path,
        bool expected)
    {
        Assert.Equal(expected, ShortcutHelper.IsInternetShortcutPath(path));
    }

    [Theory]
    [InlineData(@"C:\icons\game.ico", @"C:\icons\game.ico", null)]
    [InlineData(@"C:\Windows\System32\shell32.dll,-5", @"C:\Windows\System32\shell32.dll", -5)]
    [InlineData("\"C:\\icons\\game,alternate.dll\", 12", @"C:\icons\game,alternate.dll", 12)]
    public void SplitIconLocation_PreservesSignedIndexesAndQuotedCommaPaths(
        string iconLocation,
        string expectedPath,
        int? expectedIndex)
    {
        var (path, index) = IconHelper.SplitIconLocation(iconLocation);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void RelativeIconLocation_ResolvesBesideShortcutAndRejectsMalformedPath()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-relative-url-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string shortcutPath = Path.Combine(temporaryDirectory, "game.url");
            string iconPath = Path.Combine(temporaryDirectory, "game.ico");
            File.WriteAllText(shortcutPath, "[InternetShortcut]\nURL=https://example.invalid/\n");
            File.WriteAllBytes(iconPath, [0]);

            Assert.True(IconHelper.TryResolveRelativeIconLocation(
                shortcutPath,
                "game.ico",
                out string resolvedPath));
            Assert.Equal(Path.GetFullPath(iconPath), resolvedPath);
            Assert.False(IconHelper.TryResolveRelativeIconLocation(
                shortcutPath,
                "invalid\0icon.ico",
                out _));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void InternetShortcutIconPolicy_IsShellFirstOverlayAwareAndUsedBySearch()
    {
        string iconHelper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        string fileMetaService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileMetaService.cs"));

        Assert.Contains(
            "CreateInternetShortcutIconSource(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "includeOverlays: !hideShortcutArrowOverlay",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "InternetShortcutIconStrategyVersion",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveInternetShortcutFallbackSourceAsync(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutHelper.IsShortcutPath(item.DetailPath)",
            fileMetaService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutIconResolution_IsBoundedAndCacheInvalidationAvoidsShellReads()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        int getIconStart = source.IndexOf(
            "public static async Task<BitmapImage?> GetIconAsync(",
            StringComparison.Ordinal);
        int clearCacheStart = source.IndexOf(
            "public static void ClearIconCache(",
            getIconStart,
            StringComparison.Ordinal);
        int clearCacheEnd = source.IndexOf(
            "private static void InvalidateShellIconCache(",
            clearCacheStart,
            StringComparison.Ordinal);
        Assert.True(getIconStart >= 0);
        Assert.True(clearCacheStart > getIconStart);
        Assert.True(clearCacheEnd > clearCacheStart);

        string getIcon = source[getIconStart..clearCacheStart];
        Assert.Contains(
            "await ResolveIconSourceWithCacheKeyAsync(",
            getIcon,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveIconSource(path",
            getIcon,
            StringComparison.Ordinal);

        string clearCache = source[clearCacheStart..clearCacheEnd];
        Assert.DoesNotContain(
            "ResolveIconSource(",
            clearCache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists(", clearCache, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(", clearCache, StringComparison.Ordinal);

        Assert.Contains(
            "BoundedBackgroundWorkScheduler.SharedShell",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IconSourceResolutionTimeout",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PidlShortcutIconResolution_UsesIsolatedIconOnlyModeAndRejectsBlankIcons()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        string proxy = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ShellThumbnailProxy.cs"));

        Assert.Contains(
            "return new IconSource(path, UsesShellItemIcon: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellThumbnailProxy.TryLoadIconAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryLoadHighResolutionShellItemIconAsync(\n" +
            "                    originalSourcePath)",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "loadIconSource.UsesShellItemIcon",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EncodeVisibleBitmapAsPng(bitmap)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisibleBitmapPayload(output)",
            proxy,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    public void SolutionShortcut_ExtractsAVisibleIconWhenArrowIsHidden(
        string extension)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-solution-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string targetPath = Path.Combine(
                temporaryDirectory,
                $"test-solution{extension}");
            File.Copy(
                TestPaths.FromRepository("DeskBox.sln"),
                targetPath,
                overwrite: true);
            string shortcutPath = Path.Combine(
                temporaryDirectory,
                $"test-solution{extension}.lnk");
            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                targetPath,
                "solution icon regression");

            MethodInfo resolveMethod = typeof(IconHelper).GetMethod(
                "ResolveIconSource",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Icon source resolver was not found.");
            object iconSource = resolveMethod.Invoke(
                null,
                [shortcutPath, true])
                ?? throw new InvalidOperationException(
                    "Icon source resolver returned null.");
            MethodInfo loadMethod = typeof(IconHelper)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method =>
                    method.Name == "LoadIconBytes" &&
                    method.GetParameters().Length == 2);
            byte[] iconBytes = Assert.IsType<byte[]>(
                loadMethod.Invoke(null, [iconSource, false]));

            using var stream = new MemoryStream(iconBytes);
            using var bitmap = new Bitmap(stream);
            Assert.True(
                HasVisiblePixels(bitmap),
                $"The {extension} shortcut icon decoded as transparent.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
