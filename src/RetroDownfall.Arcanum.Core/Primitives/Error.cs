using System.Collections.Generic;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Primitives;

public readonly record struct Error(
    string Code,
    string Message,
    IReadOnlyList<ConfigurationValidationError>? Details = null)
{
    /// <summary>
    /// W4.1: a defensive, non-downcastable read-only copy of the supplied details. Returning a plain
    /// <c>List&lt;&gt;</c> as <see cref="IReadOnlyList{T}"/> let a consumer cast it back and mutate the
    /// error after construction; a <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>
    /// closes that. (Note: record-struct equality still compares this by reference; full structural
    /// equality is out of scope — <c>Details</c> is rarely set and <see cref="None"/> carries null.)
    /// </summary>
    public IReadOnlyList<ConfigurationValidationError>? Details { get; init; } =
        Details is null ? null : System.Array.AsReadOnly<ConfigurationValidationError>([.. Details]);

    /// <summary>In-process, content-free context admission data; never part of the error wire shape.</summary>
    [JsonIgnore]
    public ContextOverflowDetails? ContextOverflow { get; init; }

    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>
    /// Whether this error has exactly the field values of <see cref="None"/>: empty code, empty
    /// message, no details and no context overflow. It is the same answer as <c>this == None</c>, read from the
    /// fields so a caller never runs the generated record comparison, which would compare
    /// <see cref="Details"/> through its interface.
    /// </summary>
    [JsonIgnore]
    public bool IsNone => Code is { Length: 0 } && Message is { Length: 0 } && Details is null && ContextOverflow is null;
}

/// <summary>Only configured public identities and numeric context-window accounting, never prompt or response text.</summary>
public readonly record struct ContextOverflowDetails(
    string Provider,
    string Model,
    int InputTokens,
    int ReservedTokens,
    int TotalTokens,
    int WindowTokens)
{
    public const string Remedy = "Shorten the prompt, run arcanum session compact, start a new Session, choose a model with a larger context window, or raise providers[].contextWindowLimit to the model's actual capacity.";

    public string ToPublicMessage() => FormattableString.Invariant(
        $"Context budget exceeded for provider '{PublicIdentifier(Provider, 64)}', model '{PublicIdentifier(Model, 128)}': {TotalTokens} accounted tokens ({InputTokens} input + {ReservedTokens} reserved) exceed the {WindowTokens}-token window. {Remedy}");

    private static string PublicIdentifier(string value, int maxUtf8Bytes)
    {
        string bounded = Utf8Truncation.TruncateToUtf8ByteBudget(Utf8Truncation.NormalizeInvalidUtf16(value ?? string.Empty), maxUtf8Bytes);

        return string.Concat(bounded.Select(static character => char.IsControl(character)
            || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format
            || character is '\u2028' or '\u2029' or '\'' ? '_' : character));
    }
}

/// <summary>Shared operator guidance for every enforced daily budget refusal.</summary>
public static class BudgetErrorMessages
{
    public const string Guidance = "The budget day is UTC; a new accounting day starts at 00:00 UTC. Outstanding reservations from prior days may still count. Review Arcanum:Cost:Budget:DailyLimitUsd and raise it if appropriate.";
}
