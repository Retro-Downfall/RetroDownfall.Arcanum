using System.Reflection;

using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.Collections;

public sealed class HostedProducerAnalysisCollectionTests
{

    [Fact]
    public void Whole_program_producer_analysis_is_isolated_from_parallel_suite()
    {

        CustomAttributeData collection = Assert.Single(
            typeof(HostedGrimoireProducerInventoryTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal(
            "HostedProducerAnalysis",
            collection.ConstructorArguments[0].Value);

        CollectionDefinitionAttribute definition = Assert.IsType<CollectionDefinitionAttribute>(
            typeof(HostedProducerAnalysisCollection)
                .GetCustomAttribute<CollectionDefinitionAttribute>());

        Assert.True(definition.DisableParallelization);

    }

}
