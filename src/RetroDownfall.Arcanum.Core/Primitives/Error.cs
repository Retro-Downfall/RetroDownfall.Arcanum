using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Primitives;

public readonly record struct Error(
    string Code,
    string Message,
    IReadOnlyList<ConfigurationValidationError>? Details = null)
{

    public static readonly Error None = new(string.Empty, string.Empty);

}
