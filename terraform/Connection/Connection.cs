using Zerg.Core;

namespace terraform;

public sealed class Connection : ConnectionBase
{
    public Connection(int writeSlabSize = 1024 * 16) : base(writeSlabSize) { }
}
