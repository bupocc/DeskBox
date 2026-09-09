using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves Explorer thumbnails and Shell-item icons in a short-lived native
/// process. No Shell handler DLL is ever loaded into the DeskBox process, and
/// a hung or crashing handler is contained by the per-request timeout.
/// </summary>
internal static class ShellThumbnailProxy
{
    private enum ShellImageMode
    {
        Thumbnail,
        Icon,
        IconWithOverlays
    }

    private readonly record struct BitmapPayloadInfo(
        int Width,
        int Height,
        int SignedHeight,
        int PixelOffset,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY)
    {
        public int VisibleWidth => MaxX - MinX + 1;
        public int VisibleHeight => MaxY - MinY + 1;
    }

    internal const string ExecutableName = "DeskBox.ThumbnailProxy.exe";
    private const string ThumbnailHandlerClassId =
        "{e357fccd-a995-4576-b01f-234630154e96}";
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumFailureEntries = 256;
    private const long FailureRetryDelayMilliseconds = 30_000;
    private static readonly TimeSpan ExtractionTimeout =
        TimeSpan.FromMilliseconds(2500);
    private static readonly ConcurrentDictionary<string, bool>
        s_registeredProviderByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long>
        s_recentFailures = new(StringComparer.OrdinalIgnoreCase);
    private static int s_missingExecutableLogged;

    public static Task<bool> HasRegisteredThumbnailProviderAsync(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension) ||
            IsExcludedExtension(extension))
        {
            return Task.FromResult(false);
        }

        if (s_registeredProviderByExtension.TryGetValue(
                extension,
                out bool cached))
        {
            return Task.FromResult(cached);
        }

        return Task.Run(() => s_registeredProviderByExtension.GetOrAdd(
            extension,
            QueryRegisteredThumbnailProvider));
    }

    public static async Task<byte[]?> TryLoadAsync(
        string path,
        int requestedSize)
    {
        return await TryLoadAsync(path, requestedSize, ShellImageMode.Thumbnail);
    }

    public static async Task<byte[]?> TryLoadIconAsync(
        string path,
        int requestedSize,
        bool includeOverlays = false)
    {
        return await TryLoadAsync(
            path,
            requestedSize,
            includeOverlays
                ? ShellImageMode.IconWithOverlays
                : ShellImageMode.Icon);
    }

    private static async Task<byte[]?> TryLoadAsync(
        string path,
        int requestedSize,
        ShellImageMode mode)
    {
        string normalizedPath = NormalizePath(path);
        string failureKey = BuildFailureKey(normalizedPath, mode);
        if (IsRecentFailure(failureKey))
        {
            return null;
        }

        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            ExecutableName);
        if (!File.Exists(executablePath))
        {
            if (Interlocked.Exchange(ref s_missingExecutableLogged, 1) == 0)
            {
                App.Log(
                    $"[ShellThumbnailProxy] Native proxy is missing: " +
                    $"{executablePath}");
            }

            RecordFailure(failureKey);
            return null;
        }

        int normalizedSize = Math.Clamp(requestedSize, 24, 512);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (mode is ShellImageMode.Icon or ShellImageMode.IconWithOverlays)
        {
            startInfo.ArgumentList.Add(
                mode == ShellImageMode.IconWithOverlays
                    ? "--icon-with-overlays"
                    : "--icon-only");
        }

        startInfo.ArgumentList.Add(normalizedPath);
        startInfo.ArgumentList.Add(normalizedSize.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                RecordFailure(failureKey);
                return null;
            }
        }
        catch (Exception ex)
        {
            RecordFailure(failureKey);
            App.Log(
                $"[ShellThumbnailProxy] Start failed mode={mode} " +
                $"path={normalizedPath}: " +
                ex.Message);
            return null;
        }

        Task<byte[]> outputTask = ReadBoundedOutputAsync(
            process.StandardOutput.BaseStream,
            MaximumPayloadBytes);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(
            ExtractionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            await ObserveOutputAsync(outputTask, errorTask);
            RecordFailure(failureKey);
            App.Log(
                $"[ShellThumbnailProxy] Extraction timed out " +
                $"timeoutMs={ExtractionTimeout.TotalMilliseconds:0} " +
                $"mode={mode} path={normalizedPath}");
            return null;
        }

        byte[] output;
        string error;
        try
        {
            output = await outputTask;
            error = await errorTask;
        }
        catch (Exception ex)
        {
            TryKill(process);
            RecordFailure(failureKey);
            App.Log(
                $"[ShellThumbnailProxy] Invalid proxy output " +
                $"mode={mode} path={normalizedPath}: {ex.Message}");
            return null;
        }

        if (process.ExitCode != 0 || !IsVisibleBitmapPayload(output))
        {
            RecordFailure(failureKey);
            App.LogVerbose(
                $"[ShellThumbnailProxy] No usable image exit={process.ExitCode} " +
                $"mode={mode} path={normalizedPath} error={error.Trim()}");
            return null;
        }

        if (mode is ShellImageMode.Icon or ShellImageMode.IconWithOverlays)
        {
            byte[]? normalizedOutput = NormalizeIconPayload(output);
            if (normalizedOutput is null)
            {
                RecordFailure(failureKey);
                App.LogVerbose(
                    $"[ShellThumbnailProxy] Unable to normalize Shell-item icon " +
                    $"path={normalizedPath}");
                return null;
            }

            if (normalizedOutput.Length != output.Length)
            {
                App.LogVerbose(
                    $"[ShellThumbnailProxy] Cropped padded Shell-item icon " +
                    $"path={normalizedPath}");
            }

            output = normalizedOutput;
        }

        s_recentFailures.TryRemove(failureKey, out _);
        return output;
    }

    public static void Invalidate(string path)
    {
        string normalizedPath = NormalizePath(path);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.Thumbnail),
            out _);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.Icon),
            out _);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.IconWithOverlays),
            out _);
    }

    public static void ClearTransientFailures()
    {
        s_recentFailures.Clear();
    }

    private static bool QueryRegisteredThumbnailProvider(string extension)
    {
        try
        {
            using RegistryKey? extensionKey =
                Registry.ClassesRoot.OpenSubKey(extension);
            string? programmaticId = extensionKey?.GetValue(null) as string;
            string? perceivedType = extensionKey?.GetValue(
                "PerceivedType") as string;

            return HasHandler(extension) ||
                HasHandler($"SystemFileAssociations\\{extension}") ||
                (!string.IsNullOrWhiteSpace(programmaticId) &&
                 HasHandler(programmaticId)) ||
                (!string.IsNullOrWhiteSpace(perceivedType) &&
                 HasHandler($"SystemFileAssociations\\{perceivedType}"));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasHandler(string classPath)
    {
        using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(
            $"{classPath}\\ShellEx\\{ThumbnailHandlerClassId}");
        return key?.GetValue(null) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsExcludedExtension(string extension) =>
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".url", StringComparison.OrdinalIgnoreCase);

    internal static async Task<byte[]> ReadBoundedOutputAsync(
        Stream stream,
        int maximumBytes)
    {
        // The native proxy emits one BMP with its total length in the file
        // header. Read directly into the final array to avoid MemoryStream's
        // growth buffers and the additional full-size ToArray copy.
        byte[] header = new byte[14];
        int headerBytes = await stream.ReadAtLeastAsync(
            header,
            header.Length,
            throwOnEndOfStream: false).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return [];
        }

        if (headerBytes != header.Length ||
            header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException(
                "The thumbnail proxy returned an invalid bitmap header.");
        }

        uint declaredSize = BitConverter.ToUInt32(header, 2);
        if (declaredSize < 138 || declaredSize > maximumBytes)
        {
            throw new InvalidDataException(
                "The thumbnail proxy payload size was outside its limit.");
        }

        byte[] output = new byte[(int)declaredSize];
        header.CopyTo(output, 0);
        await stream.ReadExactlyAsync(
            output.AsMemory(header.Length)).ConfigureAwait(false);
        if (await stream.ReadAsync(header.AsMemory(0, 1)).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException(
                "The thumbnail proxy returned data beyond its bitmap payload.");
        }

        return output;
    }

    internal static bool IsVisibleBitmapPayload(byte[] bytes)
    {
        return TryReadVisibleBitmapPayload(bytes, out _);
    }

    internal static bool IsLikelyPaddedIconPayload(byte[] bytes)
    {
        return TryReadVisibleBitmapPayload(
                   bytes,
                   out BitmapPayloadInfo payload) &&
               IconBitmapQuality.IsLikelyPadded(
                   payload.Width,
                   payload.Height,
                   payload.VisibleWidth,
                   payload.VisibleHeight);
    }

    /// <summary>
    /// Crops the transparent border from a Shell icon only when the visible
    /// artwork is clearly a small glyph inside a Jumbo canvas. The normalized
    /// payload remains a 32-bit top-down BMP so the caller can decode it through
    /// the same BitmapImage path as an unmodified proxy result.
    /// </summary>
    internal static byte[]? NormalizeIconPayload(byte[] bytes)
    {
        if (!TryReadVisibleBitmapPayload(
                bytes,
                out BitmapPayloadInfo payload))
        {
            return null;
        }

        if (!IconBitmapQuality.IsLikelyPadded(
                payload.Width,
                payload.Height,
                payload.VisibleWidth,
                payload.VisibleHeight))
        {
            return bytes;
        }

        try
        {
            int cropWidth = payload.VisibleWidth;
            int cropHeight = payload.VisibleHeight;
            int rowByteCount = checked(cropWidth * 4);
            int pixelByteCount = checked(rowByteCount * cropHeight);
            int outputSize = checked(payload.PixelOffset + pixelByteCount);
            byte[] normalized = new byte[outputSize];
            Buffer.BlockCopy(
                bytes,
                0,
                normalized,
                0,
                payload.PixelOffset);
            WriteInt32(normalized, 2, outputSize);
            WriteInt32(normalized, 18, cropWidth);
            WriteInt32(normalized, 22, -cropHeight);
            WriteInt32(normalized, 34, pixelByteCount);

            int sourceRowByteCount = checked(payload.Width * 4);
            for (int y = 0; y < cropHeight; y++)
            {
                int sourceY = payload.SignedHeight < 0
                    ? payload.MinY + y
                    : payload.MaxY - y;
                int sourceOffset = checked(
                    payload.PixelOffset +
                    (sourceY * sourceRowByteCount) +
                    (payload.MinX * 4));
                int destinationOffset = checked(
                    payload.PixelOffset + (y * rowByteCount));
                Buffer.BlockCopy(
                    bytes,
                    sourceOffset,
                    normalized,
                    destinationOffset,
                    rowByteCount);
            }

            return normalized;
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[ShellThumbnailProxy] Icon payload crop failed: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadVisibleBitmapPayload(
        byte[] bytes,
        out BitmapPayloadInfo payload)
    {
        payload = default;
        if (bytes.Length < 138 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return false;
        }

        uint declaredSize = BitConverter.ToUInt32(bytes, 2);
        uint pixelOffset = BitConverter.ToUInt32(bytes, 10);
        uint dibHeaderSize = BitConverter.ToUInt32(bytes, 14);
        int signedHeight = BitConverter.ToInt32(bytes, 22);
        ushort bitsPerPixel = BitConverter.ToUInt16(bytes, 28);
        long absoluteHeight = Math.Abs((long)signedHeight);
        int parsedWidth = BitConverter.ToInt32(bytes, 18);
        if (declaredSize != bytes.Length ||
            dibHeaderSize < 40 ||
            parsedWidth <= 0 ||
            absoluteHeight <= 0 ||
            absoluteHeight > int.MaxValue ||
            bitsPerPixel != 32 ||
            pixelOffset < 54 ||
            pixelOffset >= bytes.Length)
        {
            return false;
        }

        long pixelByteCount = (long)parsedWidth * absoluteHeight * 4;
        if (pixelByteCount > bytes.Length - pixelOffset)
        {
            return false;
        }

        int pixelStart = checked((int)pixelOffset);
        int rowByteCount = checked(parsedWidth * 4);
        int parsedHeight = (int)absoluteHeight;
        int minX = parsedWidth;
        int minY = parsedHeight;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < parsedHeight; y++)
        {
            int rowStart = checked(pixelStart + (y * rowByteCount));
            for (int x = 0; x < parsedWidth; x++)
            {
                if (bytes[rowStart + (x * 4) + 3] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return false;
        }

        payload = new BitmapPayloadInfo(
            parsedWidth,
            parsedHeight,
            signedHeight,
            checked((int)pixelOffset),
            minX,
            minY,
            maxX,
            maxY);
        return true;
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
    }

    private static string BuildFailureKey(
        string normalizedPath,
        ShellImageMode mode) => $"{mode}:{normalizedPath}";

    private static bool IsRecentFailure(string path)
    {
        if (!s_recentFailures.TryGetValue(path, out long failedAt))
        {
            return false;
        }

        if (Environment.TickCount64 - failedAt < FailureRetryDelayMilliseconds)
        {
            return true;
        }

        s_recentFailures.TryRemove(path, out _);
        return false;
    }

    private static void RecordFailure(string path)
    {
        if (s_recentFailures.Count >= MaximumFailureEntries &&
            !s_recentFailures.ContainsKey(path))
        {
            s_recentFailures.Clear();
        }

        s_recentFailures[path] = Environment.TickCount64;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task ObserveOutputAsync(
        Task<byte[]> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch
        {
        }
    }
}
