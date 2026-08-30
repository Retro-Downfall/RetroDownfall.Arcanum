using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal enum ApprenticeStreamFrameDisposition
{
    Ignore,
    Result,
    Error,
    ToolCall,
    ToolResult,
    ToolError,
    Warded,
    WardResolved,
}

internal static class ApprenticeStreamFramePolicy
{
    public static bool IsTerminalToolDenial(IntelligenceEvent frame) =>
        frame.Type == IntelligenceEventType.ToolResult && frame.ToolDenied;

    public static ApprenticeStreamFrameDisposition Classify(IntelligenceEventType type) =>
        type switch
        {
            IntelligenceEventType.Result => ApprenticeStreamFrameDisposition.Result,
            IntelligenceEventType.Error => ApprenticeStreamFrameDisposition.Error,
            IntelligenceEventType.ToolCall => ApprenticeStreamFrameDisposition.ToolCall,
            IntelligenceEventType.ToolResult => ApprenticeStreamFrameDisposition.ToolResult,
            IntelligenceEventType.ToolError => ApprenticeStreamFrameDisposition.ToolError,
            IntelligenceEventType.Warded => ApprenticeStreamFrameDisposition.Warded,
            IntelligenceEventType.WardResolved => ApprenticeStreamFrameDisposition.WardResolved,
            IntelligenceEventType.Reasoning => ApprenticeStreamFrameDisposition.Ignore,
            IntelligenceEventType.Status
                or IntelligenceEventType.SessionBound
                or IntelligenceEventType.ConversationBound
                or IntelligenceEventType.Token
                => ApprenticeStreamFrameDisposition.Ignore,
            _ => ApprenticeStreamFrameDisposition.Ignore,
        };
}
