namespace dogrider.Protocol;

public enum FrameType
{
    None,
    Text,
    Binary,
    Continue,
    Close,
    Ping,
    Pong,
    Error,
}
