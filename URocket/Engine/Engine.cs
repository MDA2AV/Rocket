// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

using System.Collections.Concurrent;

namespace URocket.Engine;

public sealed partial class Engine {
    
    private const int c_bufferRingGID = 1;
    
    // Socket
    public string Ip { get; private set; } = "0.0.0.0";
    public ushort Port { get; private set; } = 8080;
    public int Backlog { get; private set; } = 65535;
    
    // Reactor
    private int _nReactors;
    
    public Engine() {
        _nReactors = 16;

        ReactorQueues = new ConcurrentQueue<int>[_nReactors];
        ReactorConnectionCounts = new long[_nReactors];
        ReactorRequestCounts = new long[_nReactors];
    }
}