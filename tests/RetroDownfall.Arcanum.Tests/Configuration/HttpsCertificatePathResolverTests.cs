using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class HttpsCertificatePathResolverTests
{

    private static string Home =>
        global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_returns_input_for_null_or_whitespace(string? input)
    {

        Assert.Equal(input, HttpsCertificatePathResolver.Resolve(input));

    }

    [Fact]
    public void Resolve_expands_bare_tilde_to_home()
    {

        string? resolved = HttpsCertificatePathResolver.Resolve("~");

        Assert.Equal(Path.GetFullPath(Home), resolved);

    }

    [Fact]
    public void Resolve_expands_tilde_slash_prefix()
    {

        string? resolved = HttpsCertificatePathResolver.Resolve("~/certs/localhost.pfx");

        Assert.Equal(Path.GetFullPath(Path.Combine(Home, "certs", "localhost.pfx")), resolved);

    }

    [Fact]
    public void Resolve_expands_tilde_backslash_prefix()
    {

        string? resolved = HttpsCertificatePathResolver.Resolve("~\\certs\\localhost.pfx");

        Assert.Equal(Path.GetFullPath(Path.Combine(Home, "certs", "localhost.pfx")), resolved);

    }

    [Fact]
    public void Resolve_returns_absolute_path_unchanged()
    {

        string absolute = OperatingSystem.IsWindows()
            ? "C:\\certs\\localhost.pfx"
            : "/etc/arcanum/localhost.pfx";

        string? resolved = HttpsCertificatePathResolver.Resolve(absolute);

        Assert.Equal(Path.GetFullPath(absolute), resolved);

    }

    [Fact]
    public void Resolve_does_not_expand_tilde_prefixed_name()
    {

        // "~foo" is not a home reference; it must not be expanded to a home-relative path.
        string? resolved = HttpsCertificatePathResolver.Resolve("~foo/bar");

        Assert.Equal(Path.GetFullPath("~foo/bar"), resolved);

        Assert.False(
            resolved!.StartsWith(Path.Combine(Home, "foo"), StringComparison.Ordinal),
            "Unexpected home expansion of ~foo");

    }

}
