using System.Net;
using Gmux.Core.Models;
using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class UpdateDownloadServiceTests : IDisposable
{
    private readonly string _tempDir;

    public UpdateDownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gray-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static UpdateCheckResult MakeResult(string url = "https://example.com/gray.msi") =>
        new(
            IsConfigured: true,
            IsUpdateAvailable: true,
            CurrentVersion: "0.1.0",
            LatestVersion: "v0.2.0",
            ReleaseUrl: "https://example.com/release",
            Message: "Update available",
            MsiAssetUrl: url,
            MsiAssetSizeBytes: 1024);

    [Fact]
    public async Task DownloadAsync_HappyPath_WritesFileAndReportsProgress()
    {
        var payload = new byte[1024];
        new Random(42).NextBytes(payload);

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);
        var progressValues = new List<double>();
        var progress = new Progress<double>(p => progressValues.Add(p));

        var path = await service.DownloadAsync(MakeResult(), progress, CancellationToken.None);

        Assert.True(File.Exists(path));
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(payload, written);
        Assert.NotEmpty(progressValues);
        Assert.Equal(1.0, progressValues[^1], precision: 2);
    }

    [Fact]
    public async Task DownloadAsync_404_ThrowsAndLeavesNoPartialFile()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"not found\"}");
        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);

        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => service.DownloadAsync(MakeResult(), new Progress<double>(), CancellationToken.None));
        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_DeletesPartialFile()
    {
        // Handler that writes slowly so the cancellation can land mid-stream.
        var handler = new TestHttpMessageHandler(async (_, ct) =>
        {
            var stream = new MemoryStream();
            for (int i = 0; i < 1024 * 1024; i++) stream.WriteByte((byte)i);
            stream.Position = 0;
            var content = new StreamContent(stream);
            content.Headers.ContentLength = stream.Length;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync(MakeResult(), new Progress<double>(), cts.Token));

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task DownloadAsync_MissingMsiUrl_Throws()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, "");
        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);

        await Assert.ThrowsAsync<UpdateDownloadException>(
            () => service.DownloadAsync(MakeResult(url: null!), new Progress<double>(), CancellationToken.None));
    }
}
