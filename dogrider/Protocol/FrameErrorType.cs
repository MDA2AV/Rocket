namespace dogrider.Protocol;

public enum FrameErrorType
{
    None,
    Incomplete,
    InvalidOpCode,
    PayloadTooLarge,
    InvalidControlFrame,
    InvalidControlFrameLength,
    Canceled,
    ConnectionClosed,
    UndefinedBehavior,
}
