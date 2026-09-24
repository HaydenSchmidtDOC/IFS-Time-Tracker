using System.Net;
using System.Text;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public class UpdateMetadataTests
{
    [Fact]
    public void ParsesValidLatestJson()
    {
        var json = """{"version":"v0.6.0","assetUrl":"https://x/exe","sha256":"abc123","releaseUrl":"https://x/release"}""";
        var info = UpdateMetadata.Parse(json);
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 6, 0), info!.Version);
        Assert.Equal("https://x/exe", info.AssetUrl);
        Assert.Equal("abc123", info.Sha256);
        Assert.Equal("https://x/release", info.ReleaseUrl);
    }

    [Fact]
    public void ParsesVersionWithoutLeadingV()
    {
        Assert.True(UpdateMetadata.TryParseVersion("0.6.0", out var v));
        Assert.Equal(new Version(0, 6, 0), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a version")]
    [InlineData("v")]
    public void RejectsBadVersions(string tag)
    {
        Assert.False(UpdateMetadata.TryParseVersion(tag, out _));
    }

    [Fact]
    public void ReturnsNullWhenRequiredFieldMissing()
    {
        Assert.Null(UpdateMetadata.Parse("""{"version":"0.6.0"}"""));
        Assert.Null(UpdateMetadata.Parse("""{"assetUrl":"https://x"}"""));
        Assert.Null(UpdateMetadata.Parse("not json"));
    }

    [Fact]
    public void IsNewerComparesCorrectly()
    {
        Assert.True(UpdateMetadata.IsNewer(new Version(0, 5, 0), new Version(0, 6, 0)));
        Assert.False(UpdateMetadata.IsNewer(new Version(0, 6, 0), new Version(0, 6, 0)));
        Assert.False(UpdateMetadata.IsNewer(new Version(0, 7, 0), new Version(0, 6, 0)));
    }
}

/// <summary>Minimal fake HTTP handler so UpdateChecker/UpdateInstaller can be tested offline.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_respond(request));
}

public class UpdateCheckerTests
{
    private static UpdateChecker NewChecker(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new FakeHttpHandler(respond)), "owner", "repo");

    [Fact]
    public async Task ReturnsInfoWhenMetadataServed()
    {
        var json = """{"version":"0.6.0","assetUrl":"https://x/exe","sha256":"abc","releaseUrl":"https://x/r"}""";
        var checker = NewChecker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8),
        });
        var info = await checker.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 6, 0), info!.Version);
    }

    [Fact]
    public async Task ReturnsNullOnHttpError()
    {
        var checker = NewChecker(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task ReturnsNullOnBadJson()
    {
        var checker = NewChecker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8),
        });
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task ReturnsNullOnNetworkFailure()
    {
        var checker = NewChecker(_ => throw new HttpRequestException("boom"));
        Assert.Null(await checker.CheckAsync());
    }
}

public class UpdateInstallerTests
{
    private static UpdateInstaller NewInstaller(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new FakeHttpHandler(respond)));

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public async Task DownloadsAndVerifiesMatchingHash()
    {
        var payload = Encoding.UTF8.GetBytes("new exe bytes");
        var info = new UpdateInfo(new Version(0, 6, 0), "https://x/exe", Sha256Of(payload), "https://x/r");
        var installer = NewInstaller(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        var tmp = await installer.DownloadAsync(info);
        try
        {
            Assert.NotNull(tmp);
            Assert.Equal(payload, await File.ReadAllBytesAsync(tmp!));
        }
        finally
        {
            if (tmp != null) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task ReturnsNullOnHashMismatch()
    {
        var payload = Encoding.UTF8.GetBytes("new exe bytes");
        var info = new UpdateInfo(new Version(0, 6, 0), "https://x/exe", "deadbeef", "https://x/r");
        var installer = NewInstaller(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        Assert.Null(await installer.DownloadAsync(info));
    }

    [Fact]
    public async Task ReturnsNullOnHttpError()
    {
        var info = new UpdateInfo(new Version(0, 6, 0), "https://x/exe", "abc", "https://x/r");
        var installer = NewInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await installer.DownloadAsync(info));
    }
}