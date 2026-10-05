using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// <c>IWeaveService.EmbedBatchAsync</c> promises one vector per input, in input order, on success, and
/// <c>WeaveService</c> enforces that promise once at the provider boundary. A consumer that re-checks
/// the answer's length against its own input count is a second copy of that rule that can drift from
/// the first, so this inventory pins that the only count comparison in the tree lives in the service.
/// </summary>
/// <remarks>
/// Width checks stay in the consumers: <c>WeaveService</c> makes no promise about vector dimensions
/// (a provider pool can answer two widths across batches), and each consumer decides what a ragged
/// answer means for its own store. Only comparisons of the answer's vector count are flagged here, so
/// a width comparison written as <c>embedding.Vector.Length != width</c> is not a match.
/// </remarks>
public sealed class WeaveBatchContractConsumerTests
{
    private const string ServiceRelativePath = "src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs";

    private static readonly Regex AnswerLengthComparison = new(
        @"\b(?:\w+\.Value|generated)\.(?:Length|Count)\s*(?:!=|==)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Consumers_do_not_recheck_the_vector_count_the_service_already_guarantees()
    {
        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            if (!source.Names("EmbedBatchAsync("))
            {
                continue;
            }

            foreach (Match match in AnswerLengthComparison.Matches(source.Text))
            {
                offenders.Add($"{source.RelativePath} compares the vector count of an EmbedBatchAsync answer ({match.Value.Trim()})");
            }
        }

        // Named and de-duplicated so the file that regressed is not hidden below an ellipsis.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_service_is_the_one_place_that_checks_the_vector_count_of_a_provider_batch()
    {
        ProductionSource service = Assert.Single(
            ProductionSourceInventory.Sources(),
            static source => source.IsExactOwner(ServiceRelativePath));

        Assert.Contains("batchEmbeddings.Length != batch.Count", service.Text, StringComparison.Ordinal);
    }
}
