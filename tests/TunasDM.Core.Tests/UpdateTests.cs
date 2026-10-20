using System.Net;
using System.Security.Cryptography;
using System.Text;
using TunasDM.Core.Updates;

namespace TunasDM.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly Uri Api = UpdateChecker.LatestReleaseUri("test/repo");
    private static readonly byte[] SetupBytes = Encoding.ASCII.GetBytes("MZ – neues Setup");
    private static readonly string SetupHash = Convert.ToHexStringLower(SHA256.HashData(SetupBytes));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Antwortet wie GitHub: Release-JSON, Setup-Datei, optional .sha256.</summary>
    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpClient Client(string releaseJson, int status = 200, byte[]? setup = null, string? sums = null) => new(new FakeGitHub(r =>
    {
        string path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases/latest"))
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(releaseJson) };
        if (path.EndsWith(".sha256") && sums is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) };
        if (path.EndsWith(".exe"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup ?? SetupBytes) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }));

    private static string Release(string tag, string? digest = null, bool sumsAsset = false, bool prerelease = false) => $$"""
        {"tag_name":"{{tag}}","body":"Neu: Updates","html_url":"https://github.com/test/repo/releases/tag/{{tag}}",
         "draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"assets":[
          {"name":"TUNAS-Download-Manager-Setup-{{tag.TrimStart('v')}}.exe","size":{{SetupBytes.Length}},
           "browser_download_url":"https://github.com/test/repo/releases/download/{{tag}}/TUNAS-Download-Manager-Setup-{{tag.TrimStart('v')}}.exe"
           {{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}
          {{(sumsAsset ? $",{{\"name\":\"TUNAS-Download-Manager-Setup-{tag.TrimStart('v')}.exe.sha256\",\"size\":64,\"browser_download_url\":\"https://github.com/test/repo/releases/download/{tag}/x.exe.sha256\"}}" : "")}}
        ]}
        """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v0.7.0-beta.1", "0.7.0.0")]
    [InlineData("V2.0.0+abc", "2.0.0.0")]
    public void ParseVersion_ReadsTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void ParseVersion_RejectsGarbage(string? tag) => Assert.Null(UpdateChecker.ParseVersion(tag));

    [Fact]
    public async Task Check_NewerRelease_WithDigest()
    {
        using var client = Client(Release("v0.7.0", digest: "sha256:" + SetupHash.ToUpperInvariant()));
        var update = await UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 7, 0, 0), update.Version);
        Assert.Equal("TUNAS-Download-Manager-Setup-0.7.0.exe", update.SetupName);
        Assert.Equal(SetupHash, update.Sha256);
        Assert.Equal("Neu: Updates", update.Notes);
    }

    [Fact]
    public async Task Check_UsesSha256Asset_WhenNoDigest()
    {
        using var client = Client(Release("v0.7.0", sumsAsset: true), sums: $"{SetupHash}  TUNAS-Download-Manager-Setup-0.7.0.exe\n");
        var update = await UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default);
        Assert.Equal(SetupHash, update!.Sha256);
    }

    [Theory]
    [InlineData("v0.6.0", false)]  // gleich alt
    [InlineData("v0.5.9", false)]  // älter
    [InlineData("v0.7.0", true)]   // Vorabversion
    public async Task Check_IgnoresSameOlderAndPrerelease(string tag, bool prerelease)
    {
        using var client = Client(Release(tag, digest: "sha256:" + SetupHash, prerelease: prerelease));
        Assert.Null(await UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default));
    }

    [Fact]
    public async Task Check_NoRelease_ReturnsNull_ServerError_Throws()
    {
        using (var client = Client("{}", status: 404))
            Assert.Null(await UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default));
        using (var client = Client("{}", status: 503))
            await Assert.ThrowsAsync<DownloadException>(() => UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default));
    }

    [Fact]
    public async Task Download_VerifiesChecksum()
    {
        using var client = Client(Release("v0.7.0", digest: "sha256:" + SetupHash));
        var update = (await UpdateChecker.CheckAsync(client, Api, new Version(0, 6, 0, 0), default))!;

        string path = await UpdateChecker.DownloadAsync(client, update, _folder, null, default);
        Assert.Equal(SetupBytes, File.ReadAllBytes(path));

        // Manipulierte Datei: verworfen, nichts bleibt liegen.
        File.Delete(path);
        using var bad = Client(Release("v0.7.0", digest: "sha256:" + SetupHash), setup: Encoding.ASCII.GetBytes("böse"));
        await Assert.ThrowsAsync<DownloadException>(() => UpdateChecker.DownloadAsync(bad, update, _folder, null, default));
        Assert.Empty(Directory.GetFiles(_folder));

        // Ohne Prüfsumme wird gar nicht erst geladen.
        await Assert.ThrowsAsync<DownloadException>(() => UpdateChecker.DownloadAsync(client, update with { Sha256 = null }, _folder, null, default));
    }
}
