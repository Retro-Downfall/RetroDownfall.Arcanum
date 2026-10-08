using System.Net;
using System.Net.Http;
using System.Text;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using RetroDownfall.TheForge.Ux.Views.Controls;
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
            new FakeWhispersService(),
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore());

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
            new FoundryFloorViewModel(new NullLogService()),
            new ScriptedConfirmationDialogService(confirm: true));

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

    /// <summary>
    /// The image policy says it mirrors the Arcanum outbound guard, so an IPv6 address that carries a
    /// private IPv4 destination (NAT64, local-use NAT64, 6to4, Teredo, IPv4-compatible) is refused here
    /// exactly as it is there, as are multicast and reserved space.
    /// </summary>
    [Theory]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b:1::808:808")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2001:0:a00:1::f7f7:f7f7")]
    [InlineData("::7f00:1")]
    [InlineData("::ffff:0:c0a8:101")]
    [InlineData("ff0e::1234")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    public void IsPublicAddress_RefusesWhatTheOutboundGuardRefuses(string ip) =>
        Assert.False(MarkdownImageSsrfPolicy.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublicAddress_KeepsPublicTransitionAddresses(string ip) =>
        Assert.True(MarkdownImageSsrfPolicy.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("app.localhost")]
    [InlineData("APP.LOCALHOST.")]
    public void IsHostAllowed_RefusesLocalhostSubdomains(string host) =>
        Assert.False(MarkdownImageSsrfPolicy.IsHostAllowed(host));

    /// <summary>
    /// The pinned socket is the image loader's own, so it carries the same <c>NoDelay</c> the outbound
    /// guard sets: small request frames must not wait on Nagle's algorithm.
    /// </summary>
    [Fact]
    public void Pinned_sockets_disable_nagle()
    {
        using System.Net.Sockets.Socket socket = MarkdownImageSsrfPolicy.CreatePinnedSocket(
            System.Net.Sockets.AddressFamily.InterNetwork);

        Assert.True(socket.NoDelay);
    }

    /// <summary>
    /// A connect cancelled by its caller (a re-render superseding the load, or the loader's timeout)
    /// disposes the socket it opened rather than leaving it for finalization.
    /// </summary>
    [Fact]
    public async Task A_cancelled_connect_disposes_its_socket()
    {
        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        System.Net.Sockets.Socket? attempted = null;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MarkdownImageSsrfPolicy.ConnectToFirstReachableAsync(
                "image.example",
                443,
                [IPAddress.Parse("8.8.8.8")],
                TimeSpan.FromSeconds(3),
                (socket, endPoint, token) =>
                {
                    attempted = socket;

                    return socket.ConnectAsync(endPoint, token);
                },
                cancellation.Token));

        Assert.NotNull(attempted);

        Assert.True(attempted!.SafeHandle.IsClosed);
    }

    /// <summary>
    /// One resolved address that never answers (a blackholed IPv6 route, say) gives up after the
    /// per-address bound, so the next address is tried within the loader's own timeout.
    /// </summary>
    [Fact]
    public async Task An_address_that_never_answers_falls_back_to_the_next_one()
    {
        System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            IPAddress silent = IPAddress.Parse("8.8.8.8");

            List<System.Net.Sockets.Socket> attempted = [];

            Task<Stream> connecting = MarkdownImageSsrfPolicy.ConnectToFirstReachableAsync(
                "image.example",
                port,
                [silent, IPAddress.Loopback],
                TimeSpan.FromMilliseconds(200),
                async (socket, endPoint, token) =>
                {
                    attempted.Add(socket);

                    if (endPoint.Address.Equals(silent))
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }

                    await socket.ConnectAsync(endPoint, token);
                },
                CancellationToken.None).AsTask();

            await using Stream connected = await connecting.WaitAsync(TimeSpan.FromSeconds(15));

            using System.Net.Sockets.Socket accepted = await listener.AcceptSocketAsync();

            Assert.Equal(2, attempted.Count);

            Assert.True(attempted[0].SafeHandle.IsClosed);
        }
        finally
        {
            listener.Stop();
        }
    }
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

    /// <summary>
    /// A host that does not resolve is a name or network problem, not the SSRF policy at work, and the
    /// placeholder says so rather than calling the host local/private/metadata.
    /// </summary>
    [Fact]
    public async Task LoadAsync_UnresolvableHost_SaysItCouldNotBeResolved()
    {
        FakeHttpMessageHandler handler = new();

        using RemoteMarkdownImageLoader loader = new(new HttpClient(handler), ownsClient: true);

        MarkdownImageResolveResult result = await loader.LoadAsync(
            new Uri("http://no-such-image-host.invalid/a.png"),
            CancellationToken.None);

        Assert.Equal(MarkdownImageResolveStatus.Failed, result.Status);

        Assert.DoesNotContain("blocked", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("could not be resolved", result.PlaceholderReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// A proxy would resolve the image host itself, after the pinned connect had checked only the
    /// proxy's address, so the loader's own handler never uses one (as the outbound guard's does not).
    /// </summary>
    [Fact]
    public void Default_handler_pins_its_own_sockets_and_never_uses_a_proxy()
    {
        using SocketsHttpHandler handler = RemoteMarkdownImageLoader.CreateDefaultHandler();

        Assert.False(handler.UseProxy);

        Assert.False(handler.AllowAutoRedirect);

        Assert.NotNull(handler.ConnectCallback);
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

/// <summary>
/// <c>TryCreateLinkedCts</c> needs no Avalonia platform (it is pure CancellationTokenSource
/// plumbing), unlike the rest of IlluminationView, which requires a live platform to construct
/// (InitializeComponent loads compiled XAML) — this is the one piece of the view this repo's lack of
/// Avalonia.Headless does not block.
/// </summary>
public class IlluminationViewTryCreateLinkedCtsTests
{
    [Fact]
    public void TryCreateLinkedCts_WhenTheSourceIsAlreadyDisposed_ReturnsNullInsteadOfThrowing()
    {
        CancellationTokenSource source = new();

        source.Dispose();

        CancellationTokenSource? result = IlluminationView.TryCreateLinkedCts(CancellationToken.None, source);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreateLinkedCts_WhenTheSourceIsLive_ReturnsALinkedSourceObservingBoth()
    {
        using CancellationTokenSource external = new();

        using CancellationTokenSource source = new();

        using CancellationTokenSource? linked = IlluminationView.TryCreateLinkedCts(external.Token, source);

        Assert.NotNull(linked);

        Assert.False(linked.IsCancellationRequested);

        source.Cancel();

        Assert.True(linked.IsCancellationRequested);
    }
}
