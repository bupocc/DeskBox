using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WebDavBackupServiceTests
{
    [Fact]
    public async Task ComputeVersionId_IsStableAndHex()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "DeskBox");
            string id = await WebDavBackupService.ComputeVersionIdAsync(path, CancellationToken.None);
            Assert.Equal(16, id.Length);
            Assert.All(id, c => Assert.True(Uri.IsHexDigit(c)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseLatestRemoteVersion_UsesNewestDeskBoxArchive()
    {
        const string xml = "<d:multistatus xmlns:d='DAV:'>" +
            "<d:response><d:href>/dav/DeskBox-old.zip</d:href><d:propstat><d:prop><d:getlastmodified>Mon, 01 Jan 2024 00:00:00 GMT</d:getlastmodified><d:getcontentlength>10</d:getcontentlength></d:prop></d:propstat></d:response>" +
            "<d:response><d:href>/dav/DeskBox-new.zip</d:href><d:propstat><d:prop><d:getlastmodified>Tue, 02 Jan 2024 00:00:00 GMT</d:getlastmodified><d:getcontentlength>20</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>";
        WebDavBackupVersion? version = WebDavBackupService.ParseLatestRemoteVersion(xml, new Uri("https://example.test/dav/"));
        Assert.NotNull(version);
        Assert.Equal("new", version!.VersionId);
        Assert.Equal(20, version.SizeBytes);
    }
}
