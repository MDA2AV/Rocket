namespace Zerg.Core;

public interface IReactor
{
    void EnqueueReturnQ(ushort bid);
    void EnqueueFlush(int fd);
}
