namespace Playground.Setup;

/// <summary>Seeds a small asset directory so <c>file</c> mode has something to serve.</summary>
internal static class SampleAssets
{
    public static void Ensure(string dir)
    {
        Directory.CreateDirectory(dir);

        string index = Path.Combine(dir, "index.html");
        if (!File.Exists(index))
        {
            File.WriteAllText(index,
                "<!doctype html><html><head><title>ioxide</title><link rel=stylesheet href=/style.css></head>" +
                "<body><h1>Served from disk via io_uring</h1><p>Read off the reactor's ring - no thread pool.</p></body></html>");
        }

        string css = Path.Combine(dir, "style.css");
        if (!File.Exists(css))
        {
            File.WriteAllText(css, "body{font-family:system-ui;margin:3rem;color:#222}h1{color:#06c}");
        }
    }
}
