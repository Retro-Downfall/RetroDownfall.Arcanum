using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.TheForge.Core.Chronicle;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>One Chronicle timeline entry rendered from a tolerant <see cref="ChronicleFrame"/>.</summary>
public sealed partial class ChronicleEntryViewModel : ObservableObject
{

    public ChronicleEntryViewModel(ChronicleFrame frame)
    {

        Type = frame.Type;

        Timestamp = frame.Timestamp;

        Message = ResolveMessage(frame);

        DurationMs = frame.DurationMs ?? frame.TotalDurationMs;

        Error = frame.Error;

        Summary = frame.Summary;

        IsWarning = frame.IsType("eventsDropped");

        IsPassThrough = frame.IsType("toolCall")
            || frame.IsType("toolResult")
            || frame.IsType("warded")
            || frame.IsType("wardResolved");

        IconKey = ResolveIconKey(frame);

    }

    public string Type { get; }

    public DateTimeOffset Timestamp { get; }

    public string Message { get; }

    public long? DurationMs { get; }

    public string? Error { get; }

    public string? Summary { get; }

    public bool IsWarning { get; }

    public bool IsPassThrough { get; }

    public string IconKey { get; }

    private static string ResolveMessage(ChronicleFrame frame)
    {

        if (!string.IsNullOrWhiteSpace(frame.Message))
        {

            return frame.Message!;

        }

        if (!string.IsNullOrWhiteSpace(frame.Description))
        {

            return frame.Description!;

        }

        if (!string.IsNullOrWhiteSpace(frame.Summary))
        {

            return frame.Summary!;

        }

        if (!string.IsNullOrWhiteSpace(frame.Result))
        {

            return frame.Result!;

        }

        if (!string.IsNullOrWhiteSpace(frame.Error))
        {

            return frame.Error!;

        }

        if (!string.IsNullOrWhiteSpace(frame.ToolName))
        {

            return frame.ToolName!;

        }

        return frame.Type;

    }

    private static string ResolveIconKey(ChronicleFrame frame)
    {

        if (frame.IsType("toolCall"))
        {

            return "toolCall";

        }

        if (frame.IsType("toolResult"))
        {

            return "toolResult";

        }

        if (frame.IsType("warded") || frame.IsType("wardResolved"))
        {

            return "ward";

        }

        if (frame.IsType("eventsDropped"))
        {

            return "warning";

        }

        return "event";

    }

}
