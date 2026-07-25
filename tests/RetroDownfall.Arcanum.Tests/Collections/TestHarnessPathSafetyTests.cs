using System.Reflection;
using RetroDownfall.Arcanum.Tests.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Collections;

public sealed class TestHarnessPathSafetyTests
{

    [Fact]
    public void ScriptToolMultiRootTests_TrackTheirExactOwnedCleanupRoot()
    {

        FieldInfo? ownedRoot = typeof(ArcanumSpellScriptToolMultiRootTests)
            .GetField("_baseDir", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ownedRoot);

        Assert.Equal(typeof(string), ownedRoot.FieldType);

    }

    [Fact]
    public void ScriptToolMultiRootTests_DisposePreservesSiblingTempDirectory()
    {

        string sibling = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-scripttool-sibling-{Guid.NewGuid():N}");

        Directory.CreateDirectory(sibling);

        try
        {

            using (var harness = new ArcanumSpellScriptToolMultiRootTests())
            {
            }

            Assert.True(Directory.Exists(sibling));

        }
        finally
        {

            if (Directory.Exists(sibling))
            {

                Directory.Delete(sibling, recursive: true);

            }

        }

    }

}
