using ioxide.file;

namespace Ioxide.Tests;

/// <summary>Static assets: files read off the ring by path, and 404 on a miss.</summary>
internal static class FileTests
{
    public static void Register(Runner runner)
    {
        runner.Test("file: serve a baked asset", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.Files, r =>
            {
                r.AddService(assets);
                AssetReader.CreatePool(r, readers: 2, bufferBytes: 64 * 1024);
            });
            (int status, string body) = Client.Get(port, "/hello.txt");
            Assert.Equal(200, status);
            Assert.Equal("hello-asset", body);
        });

        runner.Test("file: 404 on a miss", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.Files, r =>
            {
                r.AddService(assets);
                AssetReader.CreatePool(r, readers: 2, bufferBytes: 64 * 1024);
            });
            (int status, _) = Client.Get(port, "/nope.txt");
            Assert.Equal(404, status);
        });

        // The slab path drives the core TcpConnection.ReadFileAsync - read straight into the write
        // slab, no reader pool needed.
        runner.Test("file: serve via ReadFileAsync (slab path)", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.FilesSlab, r => r.AddService(assets));
            (int status, string body) = Client.Get(port, "/hello.txt");
            Assert.Equal(200, status);
            Assert.Equal("hello-asset", body);
        });

        // A file larger than the base write slab forces ReadFileAsync to grow it, then the whole body
        // still leaves in one flush - guards the grow-and-read path.
        runner.Test("file: slab path grows the slab for a big file", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.FilesSlab, r => r.AddService(assets));
            (int status, string body) = Client.Get(port, "/big.txt");
            Assert.Equal(200, status);
            Assert.Equal(BigAsset.Length, body.Length);
            Assert.Equal(BigAsset, body);
        });
    }

    // Bigger than the harness write slab (16 KiB) so serving it exercises GrowWriteSlab, but under
    // the client's 64 KiB read buffer so the whole body comes back in one shot.
    private static readonly string BigAsset = new('x', 40_000);

    private static string SampleAssets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-assets");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello-asset");
        File.WriteAllText(Path.Combine(dir, "big.txt"), BigAsset);
        return dir;
    }
}
