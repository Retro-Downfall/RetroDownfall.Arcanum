using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>One plan step with a case-insensitive status kind for the War Table plan viewer.</summary>
public sealed partial class PlanStepViewModel : ObservableObject
{

    public PlanStepViewModel(PlanStep step)
    {

        Index = step.Index;

        Description = step.Description;

        Status = step.Status;

        Result = step.Result;

        StatusKind = ResolveKind(step.Status);

    }

    public int Index { get; }

    public string Description { get; }

    public string Status { get; }

    public string? Result { get; }

    /// <summary>pending | running | completed | failed | escalated | unknown</summary>
    public string StatusKind { get; }

    private static string ResolveKind(string status)
    {

        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {

            return "pending";

        }

        if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
        {

            return "running";

        }

        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {

            return "completed";

        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {

            return "failed";

        }

        if (string.Equals(status, "escalated", StringComparison.OrdinalIgnoreCase))
        {

            return "escalated";

        }

        return "unknown";

    }

}
