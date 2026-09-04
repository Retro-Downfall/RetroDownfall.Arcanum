using System.Globalization;

using System.Text;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The exact bytes an older build left in a checkpoint row, written out rather than serialized.
/// </summary>
/// <remarks>
/// The shapes these describe no longer exist in this build, which is the point: what still exists is
/// the rows, on installations that were interrupted before upgrading. A suite that constructed them
/// through a record this build still had would be asserting that the current code refuses its own
/// type, and would stop testing anything the day the type was deleted — which is the day the question
/// starts to matter.
///
/// <para>So the payloads are literal JSON in the durable encoding: camel-cased property names and
/// numeric enum members, exactly as the source-generated context wrote them. The digest is passed in
/// because canonical form is lowercase hexadecimal and a payload that failed the digest rule would be
/// refused for the wrong reason.</para>
/// </remarks>
internal static class RetiredCovenantCheckpoints
{

    /// <summary>The retention-mutation checkpoint version this build no longer writes or reads.</summary>
    internal const int MutationVersion = 3;

    /// <summary>The factory-erasure checkpoint version this build no longer writes or reads.</summary>
    internal const int FactoryResetVersion = 1;

    private const int CovenantResetOperation = 7;

    private const int HealthyCatalogFactoryErasureOperation = 8;

    private const int InventoryPreparedPhase = 1;

    /// <summary>A well-formed version-3 retention mutation carrying a Covenant reset arm.</summary>
    internal static byte[] Mutation(Guid operationId, string effectDigest) =>
        Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"version\":{MutationVersion},\"subtype\":\"reset-memory\",\"target\":\"5\","
                + $"\"covenant\":{{\"operationId\":\"{operationId}\",\"effectDigest\":\"{effectDigest}\","
                + $"\"operation\":{CovenantResetOperation},\"phase\":{InventoryPreparedPhase}}}}}"));

    /// <summary>A well-formed version-1 healthy-catalog factory erasure checkpoint.</summary>
    internal static byte[] FactoryReset(Guid operationId, string effectDigest) =>
        Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"version\":{FactoryResetVersion},\"operationId\":\"{operationId}\","
                + $"\"effectDigest\":\"{effectDigest}\",\"operation\":{HealthyCatalogFactoryErasureOperation},"
                + $"\"phase\":{InventoryPreparedPhase}}}"));

}
