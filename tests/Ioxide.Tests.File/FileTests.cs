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
    }

    private static string SampleAssets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-assets");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello-asset");
        return dir;
    }
}
