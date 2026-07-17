using System.Net;
using System.Net.Http;
using System.Text;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class MarkdownLinkPolicyTests
{

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("mailto:operator@example.com", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ShouldOpen_GatesSchemes(string? uri, bool expected) =>
        Assert.Equal(expected, MarkdownLinkPolicy.ShouldOpen(uri));

}

public class MarkdownImagePolicyTests
{

    [Fact]
    public void ShouldLoadRemote_HonorsToggle()
    {

        Assert.True(MarkdownImagePolicy.ShouldLoadRemote(loadRemoteImagesEnabled: true));

        Assert.False(MarkdownImagePolicy.ShouldLoadRemote(loadRemoteImagesEnabled: false));

    }

    [Fact]
    public void ShouldLoadRelativeOrLocal_IsFalse() =>
        Assert.False(MarkdownImagePolicy.ShouldLoadRelativeOrLocal());

    [Fact]
    public void FormatPlaceholder_IncludesAltAndUrl()
    {

        string text = MarkdownImagePolicy.FormatPlaceholder("banner", "https://example.com/a.png");

        Assert.Contains("banner", text, StringComparison.Ordinal);

        Assert.Contains("https://example.com/a.png", text, StringComparison.Ordinal);

    }

}

public class MarkdownSafetySanitizerTests
{

    [Fact]
    public void Sanitize_ReplacesHtml_LeavesImageSyntax()
    {

        string input = "Hello ![alt](https://example.com/x.png) and <script>alert(1)</script>";

        string output = MarkdownSafetySanitizer.Sanitize(input, out bool truncated);

        Assert.False(truncated);

        Assert.Contains("![alt](https://example.com/x.png)", output, StringComparison.Ordinal);

        Assert.DoesNotContain("<script>", output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("[HTML omitted]", output, StringComparison.Ordinal);

    }

    [Fact]
    public void Sanitize_TruncatesLargeDocuments()
    {

        string input = new('a', MarkdownSafetySanitizer.MaxPreviewChars + 100);

        string output = MarkdownSafetySanitizer.Sanitize(input, out bool truncated);

        Assert.True(truncated);

        Assert.Contains("Preview truncated", output, StringComparison.Ordinal);

        Assert.True(output.Length < input.Length + 64);

    }

    [Fact]
    public void Sanitize_KitchenSinkFixture_OmitsHtml_KeepsImageSyntaxForResolver()
    {

        string path = ResolveKitchenSinkPath();

        Assert.True(File.Exists(path), $"Missing kitchen-sink fixture at {path}");

        string markdown = File.ReadAllText(path);

        string output = MarkdownSafetySanitizer.Sanitize(markdown, out _);

        Assert.DoesNotContain("<script>", output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("![Remote alt]", output, StringComparison.Ordinal);

        Assert.Contains("[HTML omitted]", output, StringComparison.Ordinal);

    }

    private static string ResolveKitchenSinkPath()
    {

        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "illumination-kitchen-sink.md");

        if (File.Exists(path))
        {

            return path;

        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "Fixtures",
            "illumination-kitchen-sink.md"));

    }

}

public class MarkdownViewModeHelperTests
{

    [Theory]
    [InlineData(MarkdownViewMode.Source, true, false, false)]
    [InlineData(MarkdownViewMode.Split, true, true, true)]
    [InlineData(MarkdownViewMode.Preview, false, true, false)]
    public void VisibilityFlags_MatchMode(
        MarkdownViewMode mode,
        bool source,
        bool preview,
        bool splitter)
    {

        Assert.Equal(source, MarkdownViewModeHelper.IsSourceVisible(mode));

        Assert.Equal(preview, MarkdownViewModeHelper.IsPreviewVisible(mode));

        Assert.Equal(splitter, MarkdownViewModeHelper.IsSplitterVisible(mode));

    }

}

public class MarkdownDocumentContentStoreTests
{

    [Fact]
    public void Put_EvictsOldestWhenOverCapacity()
    {

        MarkdownDocumentContentStore store = new();

        for (int i = 0; i < MarkdownDocumentContentStore.Capacity + 2; i++)
        {

            store.Put($"id-{i}", $"t{i}", $"c{i}");

        }

        Assert.False(store.TryGet("id-0", out _));

        Assert.True(store.TryGet($"id-{MarkdownDocumentContentStore.Capacity + 1}", out _));

    }

    [Fact]
    public void Put_Payload_PreservesWorkspaceContext()
    {

        MarkdownDocumentContentStore store = new();

        store.Put(new MarkdownDocumentPayload(
            "id",
            "title",
            "# hi",
            WorkspaceId: "ws-1",
            RelativePath: "docs/a.md",
            BaseRelativeDirectory: "docs"));

        Assert.True(store.TryGet("id", out MarkdownDocumentPayload payload));

        Assert.Equal("ws-1", payload.WorkspaceId);

        Assert.Equal("docs/a.md", payload.RelativePath);

        Assert.Equal("docs", payload.BaseRelativeDirectory);

    }

    [Fact]
    public void Remove_DropsEntry()
    {

        MarkdownDocumentContentStore store = new();

        store.Put("a", "title", "body");

        store.Remove("a");

        Assert.False(store.TryGet("a", out _));

    }

}

public class MarkdownDocumentViewModelTests
{

    [Fact]
    public void Defaults_ToPreviewAndExposesContent()
    {

        MarkdownDocumentViewModel vm = new("id", "doc.md", "# Hi");

        Assert.Equal(MarkdownViewMode.Preview, vm.ViewMode);

        Assert.False(vm.IsSourceVisible);

        Assert.True(vm.IsPreviewVisible);

        Assert.Equal("# Hi", vm.MarkdownSource);

        Assert.False(vm.LoadRemoteImages);

        Assert.True(vm.SyncScrollEnabled);

        vm.Dispose();

    }

    [Fact]
    public void SetViewMode_UpdatesVisibility()
    {

        MarkdownDocumentViewModel vm = new("id", "doc.md", "x");

        vm.ViewMode = MarkdownViewMode.Source;

        Assert.True(vm.IsSourceVisible);

        Assert.False(vm.IsPreviewVisible);

        vm.ViewMode = MarkdownViewMode.Split;

        Assert.True(vm.IsSourceVisible);

        Assert.True(vm.IsPreviewVisible);

        Assert.True(vm.IsSplitterVisible);

        vm.Dispose();

    }

    [Fact]
    public void Dispose_RemovesFromStore()
    {

        MarkdownDocumentContentStore store = new();

        store.Put("id", "t", "c");

        MarkdownDocumentViewModel vm = new("id", "t", "c", store);

        vm.Dispose();

        Assert.False(store.TryGet("id", out _));

    }

}

public class SpellEditorMarkdownViewModeTests
{

    [Fact]
    public void Defaults_ToSource_AndTracksBody()
    {

        SpellEditorViewModel vm = new(
            "heal",
            new NullSpellEditorDataSource(),
            new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new NullTextInputDialogService(),
            new FakeWhispersService());

        Assert.Equal(MarkdownViewMode.Source, vm.ViewMode);

        Assert.True(vm.IsSourceVisible);

        Assert.False(vm.IsPreviewVisible);

        Assert.False(vm.LoadRemoteImages);

        Assert.True(vm.SyncScrollEnabled);

        vm.MarkdownBody = "## Body";

        Assert.Equal("## Body", vm.MarkdownBody);

    }

}

public class CodexMarkdownViewModeTests
{

    [Fact]
    public void Defaults_ToSource_AndDisablesScrollSync()
    {

        CodexViewModel vm = new(
            null,
            new NullCodexDataSource(),
            new FoundryFloorViewModel(new NullLogService()));

        Assert.Equal(MarkdownViewMode.Source, vm.ViewMode);

        Assert.False(vm.SyncScrollEnabled);

        Assert.False(vm.LoadRemoteImages);

        vm.Content = "# Codex";

        Assert.Equal("# Codex", vm.Content);

        Assert.True(vm.IsSourceVisible);

    }

}

public class IlluminationMarkdownPipelineTests
{

    [Fact]
    public void Parse_KitchenSink_ProducesDocumentWithoutThrowing()
    {

        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "illumination-kitchen-sink.md");

        if (!File.Exists(path))
        {

            path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "Fixtures",
                "illumination-kitchen-sink.md"));

        }

        string markdown = MarkdownSafetySanitizer.Sanitize(File.ReadAllText(path), out _);

        Markdig.Syntax.MarkdownDocument document = IlluminationMarkdownPipeline.Parse(markdown);

        Assert.NotEmpty(document);

    }

}

public class ColorCodeMarkdownCodeHighlighterTests
{

    [Theory]
    [InlineData("csharp", "csharp")]
    [InlineData("C#", "csharp")]
    [InlineData("js", "javascript")]
    [InlineData("mermaid", "markdown")]
    public void NormalizeLanguageId_Aliases(string input, string expected) =>
        Assert.Equal(expected, ColorCodeMarkdownCodeHighlighter.NormalizeLanguageId(input));

    [Fact]
    public void Highlight_UnknownLanguage_ReturnsPlainSpan()
    {

        ColorCodeMarkdownCodeHighlighter highlighter = new();

        IReadOnlyList<HighlightedSpan> spans = highlighter.Highlight("plain", "not-a-real-lang");

        Assert.Single(spans);

        Assert.Equal("plain", spans[0].Text);

        Assert.Null(spans[0].ResourceBrushKey);

    }

    [Fact]
    public void Highlight_CSharp_EmitsStyledSpans()
    {

        ColorCodeMarkdownCodeHighlighter highlighter = new();

        IReadOnlyList<HighlightedSpan> spans = highlighter.Highlight("public class Foo {}", "csharp");

        Assert.NotEmpty(spans);

        Assert.Contains(spans, static span => span.ResourceBrushKey is not null);

    }

}

public class MarkdownSourceLineMapperTests
{

    [Fact]
    public void FindNearest_ReturnsClosestPrecedingAnchor()
    {

        MarkdownSourceLineMapper mapper = new(
        [
            new MarkdownSourceBlockAnchor(0, "a"),
            new MarkdownSourceBlockAnchor(10, "b"),
            new MarkdownSourceBlockAnchor(20, "c"),
        ]);

        Assert.Equal("a", mapper.FindNearest(0)?.BlockId);

        Assert.Equal("b", mapper.FindNearest(15)?.BlockId);

        Assert.Equal("c", mapper.FindNearest(100)?.BlockId);

    }

    [Fact]
    public void FindNearest_Empty_ReturnsNull() =>
        Assert.Null(new MarkdownSourceLineMapper([]).FindNearest(5));

}

public class MarkdownImageSsrfPolicyTests
{

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("example.com", true)]
    public void IsHostAllowed_BlocksLocalAndPrivate(string host, bool expected) =>
        Assert.Equal(expected, MarkdownImageSsrfPolicy.IsHostAllowed(host));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("8.8.8.8", true)]
    public void IsPublicAddress_Classifies(string ip, bool expected) =>
        Assert.Equal(expected, MarkdownImageSsrfPolicy.IsPublicAddress(IPAddress.Parse(ip)));

}

public class MarkdownImageResolverTests
{

    [Fact]
    public void Classify_RecognizesKinds()
    {

        MarkdownImageResolver resolver = new(new FakeRemoteMarkdownImageLoader());

        Assert.Equal(MarkdownImageKind.RemoteHttp, resolver.Classify("https://example.com/a.png").Kind);

        Assert.Equal(MarkdownImageKind.Relative, resolver.Classify("./a.png").Kind);

        Assert.Equal(MarkdownImageKind.DataUri, resolver.Classify("data:image/png;base64,aa").Kind);

        Assert.Equal(MarkdownImageKind.Disallowed, resolver.Classify("file:///tmp/x.png").Kind);

    }

    [Fact]
    public async Task Resolve_Remote_Disabled_ReturnsPlaceholder()
    {

        MarkdownImageResolver resolver = new(new FakeRemoteMarkdownImageLoader());

        MarkdownImageResolveResult result = await resolver.ResolveAsync(
            new MarkdownImageReference("alt", "https://example.com/a.png", MarkdownImageKind.RemoteHttp),
            new IlluminationImageContext { LoadRemoteImages = false },
            CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Placeholder, result.Status);

        Assert.Contains("disabled", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Resolve_Relative_AlwaysPlaceholder()
    {

        MarkdownImageResolver resolver = new(new FakeRemoteMarkdownImageLoader());

        MarkdownImageResolveResult result = await resolver.ResolveAsync(
            new MarkdownImageReference("alt", "./local.png", MarkdownImageKind.Relative),
            new IlluminationImageContext
            {
                LoadRemoteImages = true,
                WorkspaceId = "ws",
                RelativePath = "docs/a.md",
                BaseRelativeDirectory = "docs",
            },
            CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Placeholder, result.Status);

        Assert.Contains("text-only", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void NormalizeRelativePath_RejectsTraversal()
    {

        string path = MarkdownImageResolver.NormalizeRelativePath("docs", "../secret.png", out bool traversal);

        Assert.True(traversal);

        Assert.Equal(string.Empty, path);

    }

    [Fact]
    public async Task Resolve_DataUri_ValidTinyPng_Succeeds()
    {

        // 1x1 PNG
        const string dataUri =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        MarkdownImageResolver resolver = new(new FakeRemoteMarkdownImageLoader());

        MarkdownImageResolveResult result = await resolver.ResolveAsync(
            new MarkdownImageReference("px", dataUri, MarkdownImageKind.DataUri),
            new IlluminationImageContext(),
            CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Success, result.Status);

        Assert.NotNull(result.Bytes);

    }

    [Fact]
    public async Task Resolve_DataUri_Svg_Rejected()
    {

        string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>"));

        MarkdownImageResolver resolver = new(new FakeRemoteMarkdownImageLoader());

        MarkdownImageResolveResult result = await resolver.ResolveAsync(
            new MarkdownImageReference("s", dataUri, MarkdownImageKind.DataUri),
            new IlluminationImageContext(),
            CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Placeholder, result.Status);

    }

}

public class RemoteMarkdownImageLoaderTests
{

    [Fact]
    public async Task LoadAsync_RejectsNonHttpScheme()
    {

        using RemoteMarkdownImageLoader loader = new(new HttpClient(new FakeHttpMessageHandler()), ownsClient: true);

        MarkdownImageResolveResult result = await loader.LoadAsync(new Uri("ftp://example.com/a.png"), CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Failed, result.Status);

    }

    [Fact]
    public async Task LoadAsync_RejectsLocalhostBeforeFetch()
    {

        FakeHttpMessageHandler handler = new();

        using RemoteMarkdownImageLoader loader = new(new HttpClient(handler), ownsClient: true);

        MarkdownImageResolveResult result = await loader.LoadAsync(new Uri("http://127.0.0.1/a.png"), CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Failed, result.Status);

        Assert.Contains("blocked", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, handler.RequestCount);

    }

    [Fact]
    public async Task LoadAsync_RejectsDisallowedContentType()
    {

        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        FakeHttpMessageHandler handler = new(png, "text/html");

        // Use example.com IP literal that is public — but SSRF DNS for example.com may resolve.
        // Fake host that IsHostAllowed accepts as hostname without DNS private: use example.com
        // and stub DNS by using a literal public IP in URL after policy host check.
        using RemoteMarkdownImageLoader loader = new(new HttpClient(handler) { BaseAddress = null }, ownsClient: true);

        // 8.8.8.8 is public; request will be attempted
        MarkdownImageResolveResult result = await loader.LoadAsync(new Uri("http://8.8.8.8/a.png"), CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Failed, result.Status);

        Assert.Contains("Content-Type", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task LoadAsync_AcceptsPngFromFakeHandler()
    {

        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        FakeHttpMessageHandler handler = new(png, "image/png");

        using RemoteMarkdownImageLoader loader = new(new HttpClient(handler), ownsClient: true);

        MarkdownImageResolveResult result = await loader.LoadAsync(new Uri("http://8.8.8.8/a.png"), CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Success, result.Status);

        Assert.NotNull(result.Bytes);

        Assert.Equal(png.Length, result.Bytes!.Length);

    }

    [Fact]
    public void IsAllowedContentType_RasterOnly()
    {

        Assert.True(RemoteMarkdownImageLoader.IsAllowedContentType("image/png"));

        Assert.False(RemoteMarkdownImageLoader.IsAllowedContentType("image/svg+xml"));

        Assert.False(RemoteMarkdownImageLoader.IsAllowedContentType("text/html"));

    }

}

internal sealed class FakeRemoteMarkdownImageLoader : IRemoteMarkdownImageLoader
{

    public Task<MarkdownImageResolveResult> LoadAsync(Uri uri, CancellationToken cancellationToken) =>
        Task.FromResult(new MarkdownImageResolveResult(
            MarkdownImageResolveStatus.Failed,
            null,
            null,
            "fake loader"));

}

public class IlluminationRenderGenerationTests
{

    [Fact]
    public void Begin_SupersedesPriorGeneration_StaleCannotPublish()
    {

        IlluminationRenderGeneration gate = new();

        int generationA = gate.Begin();

        int generationB = gate.Begin();

        // Render A started, B superseded it — A completing last must not publish.
        Assert.False(gate.IsCurrent(generationA));

        Assert.True(gate.IsCurrent(generationB));

    }

    [Fact]
    public void Prepare_SanitizesAndParsesOffUiThreadSurface()
    {

        IlluminationPreparedMarkdown prepared = IlluminationMarkdownPrepare.Prepare(
            "# Title\n\nHello <script>x</script>\n");

        Assert.False(prepared.Truncated);

        Assert.DoesNotContain("<script>", prepared.SanitizedMarkdown, StringComparison.OrdinalIgnoreCase);

        Assert.NotEmpty(prepared.Anchors);

        Assert.True(prepared.Anchors[0].SourceLine >= 0);

        Assert.Equal("b0", prepared.Anchors[0].BlockId);

    }

}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{

    private readonly byte[] _body;

    private readonly string _contentType;

    public FakeHttpMessageHandler(byte[]? body = null, string contentType = "image/png")
    {

        _body = body ?? [];

        _contentType = contentType;

    }

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {

        RequestCount++;

        HttpResponseMessage response = new(HttpStatusCode.OK)
        {

            Content = new ByteArrayContent(_body),

        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);

        return Task.FromResult(response);

    }

}
