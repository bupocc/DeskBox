using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Windows.Security.Credentials;

namespace DeskBox.Services;

/// <summary>WebDAV 备份同步结果。版本 ID 是备份 ZIP 内容的 SHA-256 前 16 位。</summary>
public sealed record WebDavBackupVersion(
    string VersionId,
    DateTimeOffset BackupTimeUtc,
    string? RemotePath,
    long SizeBytes,
    bool IsRemote);

public sealed record WebDavSyncConflict(
    WebDavBackupVersion Local,
    WebDavBackupVersion Remote);

public sealed record WebDavSyncResult(
    bool Succeeded,
    WebDavBackupVersion? UploadedVersion,
    WebDavSyncConflict? Conflict,
    string? ErrorMessage)
{
    public static WebDavSyncResult Failed(string message) => new(false, null, null, message);
}

/// <summary>
/// 只负责 WebDAV 传输与远程版本发现；本地 ZIP 的生成和恢复仍由
/// <see cref="DeskBoxDataBackupService"/> 负责，避免产生第二套恢复逻辑。
/// </summary>
public sealed class WebDavBackupService
{
    private const string CredentialResource = "DeskBox.WebDav";
    private readonly HttpClient _httpClient;

    public WebDavBackupService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public static void SavePassword(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(CredentialResource, username)); } catch { }
        vault.Add(new PasswordCredential(CredentialResource, username, password));
    }

    public static string? TryGetPassword(string username)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(CredentialResource, username);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch { return null; }
    }

    public async Task<WebDavSyncResult> SyncAsync(
        Uri endpoint,
        string username,
        string password,
        string localArchivePath,
        string remoteDirectory = "DeskBox",
        bool overwriteConflict = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!File.Exists(localArchivePath))
        {
            return WebDavSyncResult.Failed("本地备份文件不存在。");
        }

        try
        {
            string localId = await ComputeVersionIdAsync(localArchivePath, cancellationToken);
            DateTimeOffset localTime = new FileInfo(localArchivePath).LastWriteTimeUtc;
            var local = new WebDavBackupVersion(
                localId,
                localTime,
                null,
                new FileInfo(localArchivePath).Length,
                false);

            Uri baseUri = EnsureDirectoryUri(endpoint);
            Uri directoryUri = BuildDirectoryUri(baseUri, remoteDirectory);
            await EnsureRemoteDirectoryAsync(baseUri, remoteDirectory, username, password, cancellationToken);
            using HttpRequestMessage listRequest = CreateRequest(new HttpMethod("PROPFIND"), directoryUri, username, password);
            listRequest.Headers.Add("Depth", "1");
            listRequest.Content = new StringContent("", Encoding.UTF8, "application/xml");
            using HttpResponseMessage listResponse = await _httpClient.SendAsync(listRequest, cancellationToken);
            if (listResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return WebDavSyncResult.Failed($"WebDAV 远端目录不存在：{directoryUri}。请先在坚果云创建“{remoteDirectory}”目录。");
            }
            if (listResponse.StatusCode is not HttpStatusCode.MultiStatus && !listResponse.IsSuccessStatusCode)
            {
                return WebDavSyncResult.Failed($"WebDAV 目录读取失败：HTTP {(int)listResponse.StatusCode}。");
            }

            string xml = await listResponse.Content.ReadAsStringAsync(cancellationToken);
            WebDavBackupVersion? remote = ParseLatestRemoteVersion(xml, directoryUri);
            if (!overwriteConflict && remote is not null && !string.Equals(remote.VersionId, local.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                return new WebDavSyncResult(false, local, new WebDavSyncConflict(local, remote), null);
            }

            string remoteName = $"DeskBox-{local.VersionId}.zip";
            Uri uploadUri = new(directoryUri, remoteName);
            using var content = new StreamContent(File.OpenRead(localArchivePath));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using HttpRequestMessage upload = CreateRequest(HttpMethod.Put, uploadUri, username, password);
            upload.Content = content;
            using HttpResponseMessage uploadResponse = await _httpClient.SendAsync(upload, cancellationToken);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                return WebDavSyncResult.Failed($"WebDAV 上传失败：HTTP {(int)uploadResponse.StatusCode}。");
            }

            return new WebDavSyncResult(true, local with { RemotePath = uploadUri.ToString() }, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WebDavSyncResult.Failed(ex.Message);
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> TestConnectionAsync(
        Uri endpoint,
        string username,
        string password,
        string remoteDirectory = "DeskBox",
        CancellationToken cancellationToken = default)
    {
        try
        {
            Uri baseUri = EnsureDirectoryUri(endpoint);
            using HttpRequestMessage baseRequest = CreateRequest(new HttpMethod("PROPFIND"), baseUri, username, password);
            baseRequest.Headers.Add("Depth", "0");
            baseRequest.Content = new StringContent(string.Empty, Encoding.UTF8, "application/xml");
            using HttpResponseMessage baseResponse = await _httpClient.SendAsync(baseRequest, cancellationToken);
            if (!baseResponse.IsSuccessStatusCode && baseResponse.StatusCode != HttpStatusCode.MultiStatus)
            {
                return (false, $"WebDAV 根地址不可访问：HTTP {(int)baseResponse.StatusCode}。请确认地址应以 /dav/ 结尾。");
            }

            Uri directoryUri = BuildDirectoryUri(baseUri, remoteDirectory);
            if (directoryUri == baseUri)
            {
                return (true, null);
            }

            await EnsureRemoteDirectoryAsync(baseUri, remoteDirectory, username, password, cancellationToken);

            using HttpRequestMessage request = CreateRequest(new HttpMethod("PROPFIND"), directoryUri, username, password);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent("", Encoding.UTF8, "application/xml");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound
                ? (true, $"WebDAV 连接成功，但远端目录不存在：{remoteDirectory}。请先在坚果云创建该目录。")
                : response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus
                ? (true, null)
                : (false, $"WebDAV 连接失败：HTTP {(int)response.StatusCode}。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>读取远端备份，按时间倒序返回最多 10 个版本。</summary>
    public async Task<IReadOnlyList<WebDavBackupVersion>> ListRemoteVersionsAsync(
        Uri endpoint,
        string username,
        string password,
        string remoteDirectory = "DeskBox",
        CancellationToken cancellationToken = default)
    {
        Uri baseUri = EnsureDirectoryUri(endpoint);
        Uri directoryUri = BuildDirectoryUri(baseUri, remoteDirectory);
        await EnsureRemoteDirectoryAsync(baseUri, remoteDirectory, username, password, cancellationToken);
        using HttpRequestMessage request = CreateRequest(new HttpMethod("PROPFIND"), directoryUri, username, password);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<WebDavBackupVersion>();
        }

        response.EnsureSuccessStatusCode();
        string xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseRemoteVersions(xml, directoryUri).Take(10).ToArray();
    }

    /// <summary>仅创建远端目录的相对路径，不会对 WebDAV 服务根目录执行 MKCOL。</summary>
    private async Task EnsureRemoteDirectoryAsync(
        Uri baseUri,
        string remoteDirectory,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        string[] segments = (remoteDirectory ?? string.Empty)
            .Trim()
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        Uri currentUri = EnsureDirectoryUri(baseUri);
        foreach (string segment in segments)
        {
            currentUri = new Uri(currentUri, Uri.EscapeDataString(segment) + "/");
            using HttpRequestMessage request = CreateRequest(new HttpMethod("MKCOL"), currentUri, username, password);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode ||
                response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                response.StatusCode == HttpStatusCode.Conflict)
            {
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"WebDAV 远端目录创建失败（{currentUri} 返回 404）。请确认父目录存在，并且账号有创建目录权限。");
            }

            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<string> DownloadAsync(
        Uri remoteArchive,
        string username,
        string password,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, remoteArchive, username, password);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination, cancellationToken);
        return destinationPath;
    }

    public static Uri BuildRemoteArchiveUri(Uri endpoint, string remoteDirectory, string versionId)
    {
        Uri directoryUri = BuildDirectoryUri(EnsureDirectoryUri(endpoint), remoteDirectory);
        return new Uri(directoryUri, $"DeskBox-{Uri.EscapeDataString(versionId)}.zip");
    }

    internal static WebDavBackupVersion? ParseLatestRemoteVersion(string xml, Uri endpoint)
    {
        return ParseRemoteVersions(xml, endpoint).FirstOrDefault();
    }

    internal static IReadOnlyList<WebDavBackupVersion> ParseRemoteVersions(string xml, Uri endpoint)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        XNamespace d = "DAV:";
        return XDocument.Parse(xml).Descendants(d + "response")
            .Select(response =>
            {
                string href = response.Element(d + "href")?.Value ?? string.Empty;
                string hrefPath = Uri.TryCreate(href, UriKind.Absolute, out Uri? hrefUri)
                    ? hrefUri.AbsolutePath
                    : href;
                string name = Path.GetFileName(Uri.UnescapeDataString(hrefPath.TrimEnd('/')));
                if (!name.StartsWith("DeskBox-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
                string id = name["DeskBox-".Length..^4];
                if (id.Length < 8) return null;
                DateTimeOffset time = DateTimeOffset.TryParse(response.Descendants(d + "getlastmodified").FirstOrDefault()?.Value, out DateTimeOffset parsed) ? parsed : DateTimeOffset.MinValue;
                long size = long.TryParse(response.Descendants(d + "getcontentlength").FirstOrDefault()?.Value, out long parsedSize) ? parsedSize : 0;
                Uri remote = Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute)
                    ? absolute
                    : new Uri(endpoint, href.TrimStart('/'));
                return new WebDavBackupVersion(id, time.ToUniversalTime(), remote.ToString(), size, true);
            })
            .Where(version => version is not null)
            .Cast<WebDavBackupVersion>()
            .OrderByDescending(version => version.BackupTimeUtc)
            .Take(10)
            .ToArray();
    }

    internal static async Task<string> ComputeVersionIdAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string username, string password)
    {
        var request = new HttpRequestMessage(method, uri);
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return request;
    }

    private static Uri EnsureDirectoryUri(Uri endpoint)
    {
        string value = endpoint.ToString();
        return value.EndsWith('/') ? endpoint : new Uri(value + "/");
    }

    private static Uri BuildDirectoryUri(Uri endpoint, string remoteDirectory)
    {
        Uri baseUri = EnsureDirectoryUri(endpoint);
        string relative = (remoteDirectory ?? string.Empty).Trim().Trim('/');
        if (relative.Length == 0) return baseUri;
        string escaped = string.Join('/', relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(baseUri, escaped + "/");
    }
}
