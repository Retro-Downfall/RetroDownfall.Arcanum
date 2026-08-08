using System.Reflection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Tests.Compendium;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests;

/// <summary>
/// Holds the test-isolation invariant from DESIGN §13 in place: no Compendium test may read,
/// rewrite, or re-permission the developer's real <c>~/.config/arcanum</c>.
/// </summary>
[Collection("EnvVarSensitive")]
public sealed class ArcanumTestHomeScopeTests
{

    /// <summary>
    /// Every Compendium test class that redirects the profile must do it through
    /// <see cref="ArcanumTestHomeScope"/>. Setting only <c>HOME</c>/<c>USERPROFILE</c> leaves
    /// <see cref="ArcanumPaths.GrimoireDirectory"/> pointed at the real Windows known folder.
    /// </summary>
    [Theory]
    [InlineData(typeof(ConfigurationStoreSmokeTests))]
    [InlineData(typeof(GenericSettingsPreservationTests))]
    [InlineData(typeof(SaveCommandCanExecuteTests))]
    [InlineData(typeof(CancelCommandTests))]
    [InlineData(typeof(HttpsConfigurationTests))]
    public void Profile_redirecting_test_classes_own_a_testing_home_scope(
        Type testClass)
    {

        FieldInfo[] fields = testClass.GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.Contains(
            fields,
            field => field.FieldType == typeof(ArcanumTestHomeScope));

    }

    [Fact]
    public void Scope_points_the_grimoire_directory_inside_its_own_root()
    {

        using ArcanumTestHomeScope scope = new("compendium-scope-guard");

        Assert.StartsWith(scope.Root, ArcanumPaths.GrimoireDirectory, StringComparison.Ordinal);

        Assert.StartsWith(scope.Root, ArcanumPaths.CertificatesDirectory, StringComparison.Ordinal);

    }

    [Fact]
    public void Scope_restores_every_variable_it_redirected()
    {

        string?[] before = Snapshot();

        using (ArcanumTestHomeScope scope = new("compendium-scope-restore"))
        {

            Assert.Equal(scope.Root, global::System.Environment.GetEnvironmentVariable("HOME"));

            Assert.Equal(
                "Testing",
                global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));

            Assert.Equal(
                scope.Root,
                global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

        }

        Assert.Equal(before, Snapshot());

    }

    private static string?[] Snapshot() =>
        [
            global::System.Environment.GetEnvironmentVariable("HOME"),
            global::System.Environment.GetEnvironmentVariable("USERPROFILE"),
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"),
        ];

}
