namespace ioxide.httpclient;

/// <summary>
/// Where a response's body starts inside the arena, and what to do when a field section turns out
/// to be an interim (1xx) head rather than the real one.
///
/// h2 and h3 both assemble a response as "one or more field sections, then body bytes" into a
/// single growable arena, so only the callback plumbing differs between them - the offset rule is
/// the same. It used to be written out in both files, which is how h3 came to be missing the
/// interim case that h2 already handled.
/// </summary>
internal struct ResponseAssembly
{
    /// <summary>First field section seen; a later one is trailers.</summary>
    public bool HeadersDone;

    /// <summary>Arena offset the body starts at.</summary>
    public int BodyStart;

    /// <summary>Body bytes accumulated so far.</summary>
    public int BodyLength;

    /// <summary>
    /// Record the end of a field section. Returns true when it was an INTERIM (1xx) head - 103
    /// Early Hints, 100 Continue - which has now been discarded, meaning the real response is
    /// still to come on the same stream.
    /// </summary>
    public bool EndFieldSection(HttpClientResponse response)
    {
        // Keeping an interim section's bytes would leave BodyStart pointing at its headers, so the
        // final response's own headers would be handed back as the body.
        if (response.Status is >= 100 and < 200)
        {
            response.ResetForInterim();
            HeadersDone = false;
            BodyStart = 0;
            BodyLength = 0;
            return true;
        }

        if (!HeadersDone)
        {
            HeadersDone = true;
            BodyStart = response.ArenaLength;   // body bytes follow the header bytes
        }

        // A later section is TRAILERS: leaving BodyStart alone keeps the body range valid, where
        // moving it would slice past the body into the trailer bytes.
        return false;
    }

    /// <summary>The body's slice of the arena, for <see cref="HttpClientResponse.SetBodyRange"/>.</summary>
    public readonly (int Offset, int Length) BodyRange => (BodyStart, BodyLength);
}
