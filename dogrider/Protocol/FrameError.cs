namespace dogrider.Protocol;

public record FrameError(string Message, FrameErrorType ErrorType = FrameErrorType.None);
