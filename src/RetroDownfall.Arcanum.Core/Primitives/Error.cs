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

    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>
    /// Whether this error has exactly the field values of <see cref="None"/>: empty code, empty
    /// message and no details. It is the same answer as <c>this == None</c>, read from the three
    /// fields so a caller never runs the generated record comparison, which would compare
    /// <see cref="Details"/> through its interface.
    /// </summary>
    [JsonIgnore]
    public bool IsNone => Code is { Length: 0 } && Message is { Length: 0 } && Details is null;
}
