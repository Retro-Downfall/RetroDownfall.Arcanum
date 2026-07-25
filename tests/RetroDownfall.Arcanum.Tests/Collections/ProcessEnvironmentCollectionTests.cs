using System.Reflection;
using RetroDownfall.Arcanum.Tests.Intelligence.Spells;
using RetroDownfall.Arcanum.Tests.Repositories;
using RetroDownfall.Arcanum.Tests.Security;

namespace RetroDownfall.Arcanum.Tests.Collections;

public sealed class ProcessEnvironmentCollectionTests
{

    [Fact]
    public void SpellRepositoryTests_UseProcessEnvironmentCollection()
    {

        CustomAttributeData collection = Assert.Single(
            typeof(SpellRepositoryTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal("ProcessEnvironment", collection.ConstructorArguments[0].Value);

    }

    [Fact]
    public void SpellSearchServiceTests_UseProcessEnvironmentCollection()
    {

        CustomAttributeData collection = Assert.Single(
            typeof(SpellSearchServiceTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal("ProcessEnvironment", collection.ConstructorArguments[0].Value);

    }

    [Fact]
    public void DataProtectionSecretStoreTests_UseProcessEnvironmentCollection()
    {

        CustomAttributeData collection = Assert.Single(
            typeof(DataProtectionSecretStoreTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal("ProcessEnvironment", collection.ConstructorArguments[0].Value);

    }

    [Fact]
    public void OsKeychainSecretStoreTests_UseProcessEnvironmentCollection()
    {

        CustomAttributeData collection = Assert.Single(
            typeof(OsKeychainSecretStoreTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal("ProcessEnvironment", collection.ConstructorArguments[0].Value);

    }

}
