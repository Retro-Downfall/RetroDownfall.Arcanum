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
    /// Defensive copy of the supplied details so callers cannot mutate the list after constructing the error.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationError>? Details { get; init; } =
        Details is null ? null : new List<ConfigurationValidationError>(Details);

    public static readonly Error None = new(string.Empty, string.Empty);

}
