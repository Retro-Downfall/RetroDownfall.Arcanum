using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Whether a Familiar is ready to serve a turn.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FamiliarProbeStatus>))]
public enum FamiliarProbeStatus
{

    /// <summary>The binary is not on PATH and not at the configured <c>command</c>.</summary>
    NotInstalled,

    /// <summary>Installed, but not signed in — the operator runs the sign-in command themselves.</summary>
    NotConfigured,

    /// <summary>Installed and signed in against the operator's own subscription.</summary>
    Configured,

}

/// <summary>
/// How much Arcanum actually knows about a Familiar's model catalogue, stated rather than implied.
/// </summary>
/// <remarks>
/// Neither CLI ships a documented machine-readable list-models command, so <see cref="Unknown"/> is
/// the ordinary outcome today and every surface has to work in it. <see cref="Discovered"/> exists
/// so a real enumeration surface has a typed home if one appears; scraping <c>--help</c> for model
/// names is explicitly not that surface, because it breaks on every CLI release and would produce a
/// confidently wrong list.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FamiliarModelEnumeration>))]
public enum FamiliarModelEnumeration
{

    /// <summary>The CLI reported its own catalogue.</summary>
    Discovered,

    /// <summary>The operator wrote the list into <c>arcanum.json</c>.</summary>
    OperatorDeclared,

    /// <summary>Nothing authoritative is available — never a guess presented as a list.</summary>
    Unknown,

}

/// <summary>
/// The result of asking a Familiar whether it is ready, without asking it for a completion.
/// </summary>
/// <remarks>
/// Deliberately narrow. A status command may print an account e-mail, an organisation id, or local
/// paths; none of those have a field here, so they cannot reach a payload, a log, or Compendium.
/// Arcanum reports status, version, and remediation text — it never reads, copies, or proxies the
/// CLI's credential store.
/// </remarks>
/// <param name="ProviderName">The <c>arcanum.json</c> provider row this describes.</param>
/// <param name="ProviderType">The Familiar kind, as its <see cref="AiProviderKind"/> name.</param>
/// <param name="Status">Installed / signed-in state.</param>
/// <param name="Version">The version the CLI reported, when it reported one.</param>
/// <param name="Summary">One line an operator can read without knowing the internals.</param>
/// <param name="Remediation">What to do next; empty when there is nothing to do.</param>
/// <param name="RemediationCommand">A copyable command the operator runs themselves, never Arcanum.</param>
/// <param name="Enumeration">Where <paramref name="Models"/> came from, or that it is unknown.</param>
/// <param name="Models">Model ids Arcanum can offer for this provider, hidden ones included.</param>
/// <param name="HiddenModels">The operator's hide list, retained even when not currently offered.</param>
public sealed record FamiliarProbeResult(
    string ProviderName,
    string ProviderType,
    FamiliarProbeStatus Status,
    string? Version,
    string Summary,
    string Remediation,
    string? RemediationCommand,
    FamiliarModelEnumeration Enumeration,
    string[] Models,
    string[] HiddenModels);
