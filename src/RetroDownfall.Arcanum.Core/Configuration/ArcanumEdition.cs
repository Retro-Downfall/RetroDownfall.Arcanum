using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime edition / hardening mode. Resolved once at startup from <c>Arcanum:Edition</c> or
/// <c>ARCANUM_EDITION</c>. <see cref="Local"/> is the shipped default: host process tools, A2A/Conclave
/// surfaces, and diagnostic MCP invocation stay gated. <see cref="Development"/> unlocks those
/// surfaces only when accompanying startup flags are also set.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArcanumEdition>))]
public enum ArcanumEdition
{

    [JsonStringEnumMemberName("local")]
    Local = 0,

    [JsonStringEnumMemberName("development")]
    Development = 1,

}
