using System.Runtime.CompilerServices;

namespace zerg.Utils.Memory;

public static unsafe class MemoryExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyFrom(this Memory<byte> dst, byte* src, int len)
    {
        if (src == null) 
            throw new ArgumentNullException(nameof(src));
        
        if ((uint)len > (uint)dst.Length) 
            throw new ArgumentOutOfRangeException(nameof(len));

        fixed (byte* destination = dst.Span)
        {
            Buffer.MemoryCopy(src, destination, len, len);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyFromRing(this Memory<byte> dst, ref RingItem ring)
    {
        if ((uint)ring.Length > (uint)dst.Length) 
            throw new ArgumentOutOfRangeException(nameof(ring.Length));

        fixed (byte* destination = dst.Span)
        {
            Buffer.MemoryCopy(ring.Ptr, destination, ring.Length, ring.Length);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CopyFromRings(this Memory<byte> dst, RingItem[] ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        
        var total = 0;
        for (var i = 0; i < ring.Length; i++)
        {
            var len = ring[i].Length;
            if (len < 0) throw new ArgumentOutOfRangeException(nameof(ring), "Negative RingItem.Length.");
            total = checked(total + len);
        }

        if (total > dst.Length)
            throw new ArgumentOutOfRangeException(nameof(dst), "Destination too small.");

        fixed (byte* destination = dst.Span)
        {
            var offset = 0;

            for (var i = 0; i < ring.Length; i++)
            {
                var len = ring[i].Length;
                
                if (len < 0)
                    throw new ArgumentOutOfRangeException(nameof(ring), "RingItem.Length is negative.");

                if (len == 0) 
                    continue;

                ArgumentNullException.ThrowIfNull(ring[i].Ptr);

                Buffer.MemoryCopy(
                    ring[i].Ptr,
                    destination + offset,
                    dst.Length - offset,
                    len);

                offset += len;
            }
        }

        return total;
    }
}