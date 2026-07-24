namespace ioxide.h3;

/// <summary>One HTTP/3 request, fully assembled (headers + body) before the handler runs.</summary>
public sealed class H3Request
{
    public long StreamId { get; internal set; }
    public string Method { get; internal set; } = "";
    public string Path { get; internal set; } = "";
    public string Scheme { get; internal set; } = "";
    public string Authority { get; internal set; } = "";
    public List<(string Name, string Value)> Headers { get; } = [];
    public byte[] Body { get; internal set; } = [];

    internal MemoryStream? BodyBuffer;
    internal bool Complete;
}

/// <summary>One HTTP/3 response: status, headers, and an in-memory body.</summary>
public sealed class H3Response
{
    public int Status { get; init; } = 200;
    public List<(string Name, string Value)> Headers { get; } = [];
    public byte[] Body { get; init; } = [];

    public static H3Response Text(string body, int status = 200)
    {
        var h3Response = new H3Response
        {
            Status = status, 
            Body = System.Text.Encoding.UTF8.GetBytes(body) 
        };
        
        h3Response.Headers.Add(("content-type", "text/plain; charset=utf-8"));
        
        return h3Response;
    }
}
