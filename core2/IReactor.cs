namespace core2;

public interface IReactor
{
    void EnqueueReturnQ(ushort bid);
    void EnqueueFlush(int fd);
}