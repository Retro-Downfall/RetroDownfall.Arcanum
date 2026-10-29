using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class DocumentationLayoutTests
{
    [Fact]
    public void Maintained_documentation_is_undated_Markdown_directly_under_docs()
    {
        string root = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs");

        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

        Assert.NotEmpty(files);

        Assert.All(files, path =>
        {
            Assert.Equal(root, Path.GetDirectoryName(path));

            Assert.Equal(".md", Path.GetExtension(path));

            Assert.DoesNotMatch(@"20\d{2}[-.]?\d{2}[-.]?\d{2}", Path.GetFileName(path));
        });
    }
}
