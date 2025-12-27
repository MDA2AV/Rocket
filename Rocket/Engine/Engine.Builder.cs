namespace Rocket.Engine;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

public sealed partial class RocketEngine {
    private const int c_bufferRingGID = 1;
    private const string c_ip = "0.0.0.0";
    private static int s_ringEntries =  8 * 1024;
    private static int s_recvBufferSize  = 32 * 1024;
    private static int s_bufferRingEntries = 16 * 1024;     // power-of-two
    private static int s_backlog = 65535;
    private static int s_batchCQES = 4096;
    private static int s_nReactors;
    private static int s_maxConnectionsPerReactor = 8 * 1024;
    private static ushort s_port = 8080;
    private static Func<int>? s_calculateNumberReactors;
    
    public static RocketBuilder CreateBuilder() => new RocketBuilder();
    public sealed class RocketBuilder {
        private readonly RocketEngine _engine;
        public RocketBuilder() => _engine = new RocketEngine();
        public RocketEngine Build() { s_nReactors = s_calculateNumberReactors?.Invoke() ?? Environment.ProcessorCount / 2; return _engine; }
        public RocketBuilder Backlog(int backlog) { s_backlog = backlog; return this; }
        public RocketBuilder Port(ushort port) { s_port = port; return this; }
        public RocketBuilder SetRingEntries(int ringEntries) { s_ringEntries = ringEntries; return this; }
        public RocketBuilder SetBufferRingEntries(int bufferRingEntries) { s_bufferRingEntries = bufferRingEntries; return this; }
        public RocketBuilder BatchCQES(int batchCQES) { s_batchCQES = batchCQES; return this; }
        public RocketBuilder RecvBufferSize(int recvBufferSize) { s_recvBufferSize = recvBufferSize; return this; }
        public RocketBuilder ReactorQuant(Func<int>? calculateNumberReactors) { s_calculateNumberReactors = calculateNumberReactors; return this; }
    }
}