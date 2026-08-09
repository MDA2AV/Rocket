using System.Runtime.InteropServices;

namespace ioxide.nghttp3;

/// <summary>
/// Unmanaged callbacks from the shim (reactor thread, synchronously inside ih3_read_stream). Pure
/// depositors: they fill the per-stream assembly state and never dispatch or wake anything - the
/// code after the native call unwinds (DispatchReady / FireBodyWakes) acts on what they left.
/// Every byte* dies when the callback returns (nghttp3 rcbufs / our own fed buffer), so anything
/// kept is copied here, into the request arena or the body storage.
/// </summary>
public sealed partial class Nghttp3Connection
{
    private static unsafe Nghttp3Connection From(void* user)
        => (Nghttp3Connection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static unsafe void CallbackBeginHeaders(void* user, long streamId)
    {
        Nghttp3Connection connection = From(user);
        // Create-if-absent: a second header block on the same stream is a TRAILERS section and
        // must NOT replace the assembled request (fields of it are dropped in CallbackHeader).
        if (!connection._requests.ContainsKey(streamId))
        {
            connection._requests[streamId] = connection.RentRequest(streamId);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackHeader(void* user, long streamId, byte* name, nuint nameLen, byte* value, nuint valueLen)
    {
        Nghttp3Connection connection = From(user);
        if (!connection._requests.TryGetValue(streamId, out Nghttp3Request? request) || request.HeadersDone)
        {
            return;   // trailer fields: not surfaced (and never appended into a live request)
        }

        // Copy now - nghttp3 reclaims both rcbufs when this callback returns. Pseudo-headers
        // route by byte compare; nothing is decoded to text anywhere in the library.
        var headerName = new ReadOnlySpan<byte>(name, (int)nameLen);
        if (headerName.Length > 0 && headerName[0] == (byte)':')
        {
            (int Off, int Len) valueRange = request.Append(value, (int)valueLen);
            if      (headerName.SequenceEqual(":method"u8))    request.MethodR = valueRange;
            else if (headerName.SequenceEqual(":path"u8))      request.PathR = valueRange;
            else if (headerName.SequenceEqual(":scheme"u8))    request.SchemeR = valueRange;
            else if (headerName.SequenceEqual(":authority"u8)) request.AuthorityR = valueRange;
            return;
        }

        (int Off, int Len) nameRange = request.Append(name, (int)nameLen);
        (int Off, int Len) valueDataRange = request.Append(value, (int)valueLen);
        request.HeaderRanges.Add((nameRange.Off, nameRange.Len, valueDataRange.Off, valueDataRange.Len));
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndHeaders(void* user, long streamId, int fin)
    {
        Nghttp3Connection connection = From(user);
        if (!connection._requests.TryGetValue(streamId, out Nghttp3Request? request) || request.HeadersDone)
        {
            return;
        }
        request.HeadersDone = true;

        if (!connection._streaming)
        {
            return;   // buffered: body (if any) follows via CallbackData; dispatch at CallbackEndStream
        }

        // Streaming: THIS is the dispatch point. Bodyless requests (fin rode the headers) share
        // one pre-ended reader - no sink allocation, no pacing; bodied ones get a paced sink.
        if (fin != 0)
        {
            request.BodyReader = Nghttp3BodyReader.Ended;
        }
        else
        {
            var sink = new Nghttp3BodyReader(connection, streamId, ended: false);
            request.BodyReader = sink;
            connection._sinks[streamId] = sink;
            connection._quicConnection.SetStreamPaced(streamId, true);
        }

        request.Complete = true;
        connection._readyStreamIds.Add(streamId);
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackData(void* user, long streamId, byte* data, nuint dataLen)
    {
        Nghttp3Connection connection = From(user);
        if (connection._streaming)
        {
            if (connection._sinks.TryGetValue(streamId, out Nghttp3BodyReader? sink))
            {
                sink.Push(new ReadOnlySpan<byte>(data, (int)dataLen));
            }
            return;
        }

        if (connection._requests.TryGetValue(streamId, out Nghttp3Request? request))
        {
            request.AppendBody(new ReadOnlySpan<byte>(data, (int)dataLen));
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndStream(void* user, long streamId)
    {
        Nghttp3Connection connection = From(user);
        if (connection._streaming)
        {
            if (connection._sinks.Remove(streamId, out Nghttp3BodyReader? sink))
            {
                sink.End();
            }
            return;
        }

        if (connection._requests.TryGetValue(streamId, out Nghttp3Request? request) && !request.Complete)
        {
            request.Complete = true;
            connection._readyStreamIds.Add(streamId);
        }
    }

    // nghttp3 consumed bytes internally for a sync-blocked stream; on a paced stream the app owns
    // the crediting, so forward them to the window.
    [UnmanagedCallersOnly]
    private static unsafe void CallbackDeferredConsume(void* user, long streamId, nuint consumed)
    {
        Nghttp3Connection connection = From(user);
        if (connection._sinks.ContainsKey(streamId))
        {
            connection._quicConnection.ConsumeStreamData(streamId, (long)consumed);
        }
    }

    /// <summary>
    /// nghttp3 wants body bytes for a streamed response. Runs INSIDE its writev, so it resolves
    /// the writer and returns - anything that would re-enter nghttp3 (a resume) is deferred.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void CallbackReadBody(void* user, long streamId, byte** buffer, nuint* length, int* fin)
    {
        Nghttp3Connection connection = From(user);
        connection.PullStreamedBody(streamId, buffer, length, fin);
    }

}
