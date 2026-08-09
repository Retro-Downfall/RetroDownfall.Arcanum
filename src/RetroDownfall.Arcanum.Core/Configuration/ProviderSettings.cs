using System.Linq;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderSettings
{

    public string Name { get; set; } = string.Empty;

    public AiProviderKind Type { get; set; }

    /// <summary>
    /// Base URI for an <see cref="AiProviderKind.OpenAICompatible"/> provider. Not applicable to a
    /// Familiar kind, which has no endpoint to dial — see <see cref="FamiliarProviders"/>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional exact environment-variable name containing this provider's API key. When omitted,
    /// Arcanum derives <c>ARCANUM_PROVIDER_{NORMALIZED_NAME}_API_KEY</c>; secret values never enter
    /// configuration. Not applicable to a Familiar kind, whose CLI authenticates itself against the
    /// operator's own subscription.
    /// </summary>
    public string? CredentialEnvironmentVariable { get; set; }

    /// <summary>
    /// Optional path to (or alternate name for) the Familiar binary, when the operator's install is
    /// not the one <c>PATH</c> would find. Blank means the kind's default name. Familiar kinds only.
    /// </summary>
    public string? Command { get; set; }

    public IReadOnlyList<ModelEntry> Models { get; set; } = [];

    /// <summary>
    /// Model ids omitted from listings and pickers for this provider. Empty — the default — means
    /// every model the Familiar offers is available. Subtractive by design: a hidden model is still
    /// resolvable by explicit name, so this never becomes a policy control. Familiar kinds only; an
    /// <see cref="AiProviderKind.OpenAICompatible"/> row hides a model by deleting its
    /// <see cref="Models"/> entry.
    /// </summary>
    public string[] HiddenModels { get; set; } = [];

    public int ContextWindowLimit { get; set; } = 8192;

    public override string ToString()
    {
        return $"{nameof(ProviderSettings)} {{ {nameof(Name)} = {Name}, {nameof(Type)} = {Type}, {nameof(Endpoint)} = {Endpoint}, {nameof(CredentialEnvironmentVariable)} = {CredentialEnvironmentVariable ?? "null"}, {nameof(Command)} = {Command ?? "null"}, {nameof(Models)} = [{string.Join(", ", Models.Select(static m => m.SupportsVision ? $"{m.Name}(vision)" : m.Name))}], {nameof(HiddenModels)} = [{string.Join(", ", HiddenModels ?? [])}], {nameof(ContextWindowLimit)} = {ContextWindowLimit} }}";
    }

}
