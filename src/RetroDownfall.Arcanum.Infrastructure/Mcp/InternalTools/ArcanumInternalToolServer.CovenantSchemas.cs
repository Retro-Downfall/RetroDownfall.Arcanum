using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// The hand-authored input and output schemas for the two Covenant mutation tools.
/// </summary>
/// <remarks>
/// Hand-authored rather than generated, and audited on what they <em>omit</em>. Scope, Campaign id,
/// origin, lifecycle, revision, attachment ids, and every authority field are absent from both input
/// schemas, so agent-originated Global mutation is unrepresentable on the wire as well as refused by
/// the server. The output schemas carry no secret, no raw provenance content, and no operator
/// authority field (§10.14).
///
/// <para>Both tools are withheld from <c>tools/list</c> in this build, so these schemas are built
/// and held rather than advertised. They are kept exact anyway: the omissions are the contract the
/// wire has to keep whenever either tool is advertised again, and a schema that decayed while
/// nobody could see it would be the wrong thing to turn back on.</para>
/// </remarks>
internal sealed partial class ArcanumInternalToolServer
{

    /// <summary>
    /// Visible to tests for the same reason the retirement schema is: neither tool is advertised in
    /// this build, so nothing else can reach the shape to prove it still refuses to represent scope,
    /// Campaign id, origin, lifecycle, revision, or attachment identity on the wire.
    /// </summary>
    internal static JsonElement BuildProposeCovenantSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "Short stable identity for this standing preference, such as \"response.style\" or "
                + "\"build.commands\". Reusing a key replaces the proposal stored under it.");

            WriteStringProperty(
                w,
                "content",
                "The preference itself, in one plain sentence and the operator's own terms. It is stored "
                + "for the operator to review and confirm; it is never treated as an instruction until "
                + "they do.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteStringValue("content");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    /// <summary>
    /// Visible to tests because the shape it forbids outlives the advertisement that carried it: the
    /// retirement tool is withheld from <c>tools/list</c> in this build, so nothing else can reach the
    /// schema to prove it still refuses to represent scope, origin, or authority on the wire.
    /// </summary>
    internal static JsonElement BuildRetireCovenantSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "The key to retire. It must name content that was actually admitted into this exact "
                + "model call; a target you have not been shown cannot be retired here.");

            w.WriteStartObject("lane");

            w.WriteString("type", "string");

            w.WriteStartArray("enum");

            w.WriteStringValue(nameof(CovenantLane.Confirmed));

            w.WriteStringValue(nameof(CovenantLane.Proposed));

            w.WriteEndArray();

            w.WriteString(
                "description",
                "Which lane to retire: Confirmed for content the operator authored, Proposed for an "
                + "earlier proposal of yours.");

            w.WriteEndObject();

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteStringValue("lane");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    /// <summary>
    /// The shape of <c>structuredContent</c> for both Covenant tools: one staged receipt, or one
    /// typed expected failure.
    /// </summary>
    private static JsonElement BuildCovenantMutationOutputSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            w.WriteStartObject("status");

            w.WriteString("type", "string");

            w.WriteStartArray("enum");

            w.WriteStringValue(CovenantMutationStagedStatus);

            w.WriteStringValue(CovenantMutationFailedStatus);

            w.WriteEndArray();

            w.WriteString(
                "description",
                "\"staged\" means the change was accepted into this turn, not that it has been written. "
                + "It is written when this turn's reply is saved, in the same transaction, and dropped "
                + "with the turn if the reply never is. Even once written it sits in the Proposed lane "
                + "awaiting the operator's confirmation, so it is a suggestion to them rather than a "
                + "setting that now applies.");

            w.WriteEndObject();

            WriteStringProperty(w, "mutationId", "Opaque identity of the staged mutation.");

            WriteStringProperty(
                w,
                "targetId",
                "Opaque identity of the target. It is not the key and cannot be turned back into one.");

            WriteStringProperty(w, "scope", "Always \"Campaign\": there is no agent-writable Global scope.");

            WriteStringProperty(w, "lane", "The lane this mutation targets.");

            WriteStringProperty(w, "operation", "\"Set\" for a proposal, \"Retire\" for a retirement.");

            WriteIntegerProperty(
                w,
                "expectedRevision",
                "The lane revision this mutation will compare and swap against at publication.");

            WriteStringProperty(w, "renderedHash", "Hash of the compiled fragment, for a proposal.");

            WriteIntegerProperty(w, "byteCost", "Rendered byte cost of the compiled fragment, for a proposal.");

            WriteStringProperty(w, "code", "Typed failure code, present only when status is \"failed\".");

            WriteStringProperty(w, "message", "Short code-owned explanation, present only when status is \"failed\".");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("status");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

}
