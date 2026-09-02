using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RetroDownfall.TheForge.Ux.Markdown;

namespace RetroDownfall.TheForge.Ux.Views.Controls;

/// <summary>
/// The Illumination — native markdown preview surface for The Forge. Renders Markdig 1.2.0 AST to
/// Avalonia controls; never uses a WebView.
/// </summary>
public partial class IlluminationView : UserControl
{

    public static readonly StyledProperty<string?> MarkdownSourceProperty =
        AvaloniaProperty.Register<IlluminationView, string?>(nameof(MarkdownSource));

    public static readonly StyledProperty<bool> LoadRemoteImagesProperty =
        AvaloniaProperty.Register<IlluminationView, bool>(nameof(LoadRemoteImages));

    public static readonly StyledProperty<bool> SyncScrollEnabledProperty =
        AvaloniaProperty.Register<IlluminationView, bool>(nameof(SyncScrollEnabled), defaultValue: true);

    public static readonly StyledProperty<string?> WorkspaceIdProperty =
        AvaloniaProperty.Register<IlluminationView, string?>(nameof(WorkspaceId));

    public static readonly StyledProperty<string?> RelativePathProperty =
        AvaloniaProperty.Register<IlluminationView, string?>(nameof(RelativePath));

    public static readonly StyledProperty<string?> BaseRelativeDirectoryProperty =
        AvaloniaProperty.Register<IlluminationView, string?>(nameof(BaseRelativeDirectory));

    public static readonly StyledProperty<int> NavigateToSourceLineProperty =
        AvaloniaProperty.Register<IlluminationView, int>(nameof(NavigateToSourceLine), -1);

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Matches TruncationNotice's XAML default (IlluminationView.axaml); restored on every
    /// successful render so a stale failure notice cannot survive it.</summary>
    private const string TruncationNoticeDefaultText = "Preview truncated";

    private const string RenderFailedNoticeText = "Preview failed to render.";

    private static readonly MarkdownImageCache SharedImageCache = new();

    private readonly IlluminationHyperlinkCommand _hyperlinkCommand = new();

    private readonly IMarkdownImageResolver _imageResolver =
        new MarkdownImageResolver(new RemoteMarkdownImageLoader(), SharedImageCache);

    private readonly IMarkdownCodeHighlighter _highlighter = new ColorCodeMarkdownCodeHighlighter();

    private readonly IlluminationRenderGeneration _renderGeneration = new();

    private CancellationTokenSource? _debounceCts;

    private CancellationTokenSource? _renderCts;

    private MarkdigAstAvaloniaRenderer? _activeRenderer;

    private MarkdownSourceLineMapper _lineMapper = new([]);

    public IlluminationView()
    {

        InitializeComponent();

    }

    public string? MarkdownSource
    {

        get => GetValue(MarkdownSourceProperty);

        set => SetValue(MarkdownSourceProperty, value);

    }

    public bool LoadRemoteImages
    {

        get => GetValue(LoadRemoteImagesProperty);

        set => SetValue(LoadRemoteImagesProperty, value);

    }

    public bool SyncScrollEnabled
    {

        get => GetValue(SyncScrollEnabledProperty);

        set => SetValue(SyncScrollEnabledProperty, value);

    }

    public string? WorkspaceId
    {

        get => GetValue(WorkspaceIdProperty);

        set => SetValue(WorkspaceIdProperty, value);

    }

    public string? RelativePath
    {

        get => GetValue(RelativePathProperty);

        set => SetValue(RelativePathProperty, value);

    }

    public string? BaseRelativeDirectory
    {

        get => GetValue(BaseRelativeDirectoryProperty);

        set => SetValue(BaseRelativeDirectoryProperty, value);

    }

    /// <summary>Set from the source editor caret/scroll to scroll the preview to the nearest block.</summary>
    public int NavigateToSourceLine
    {

        get => GetValue(NavigateToSourceLineProperty);

        set => SetValue(NavigateToSourceLineProperty, value);

    }

    public event EventHandler<int>? GoToSourceRequested;

    public IReadOnlyList<MarkdownSourceBlockAnchor> SourceAnchors => _lineMapper is null
        ? []
        : _activeRenderer?.Anchors ?? [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {

        base.OnPropertyChanged(change);

        if (change.Property == MarkdownSourceProperty
            || change.Property == LoadRemoteImagesProperty
            || change.Property == WorkspaceIdProperty
            || change.Property == RelativePathProperty
            || change.Property == BaseRelativeDirectoryProperty)
        {

            ScheduleRender(MarkdownSource);

        }
        else if (change.Property == NavigateToSourceLineProperty)
        {

            ScrollToSourceLine(NavigateToSourceLine);

        }

    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {

        base.OnDetachedFromVisualTree(e);

        _debounceCts?.Cancel();

        _renderCts?.Cancel();

    }

    public void ScrollToSourceLine(int sourceLine)
    {

        if (!SyncScrollEnabled || sourceLine < 0)
        {

            return;

        }

        MarkdownSourceBlockAnchor? anchor = _lineMapper.FindNearest(sourceLine);

        if (anchor is null || PreviewHost.Content is not Control root)
        {

            return;

        }

        Control? target = FindByBlockId(root, anchor.BlockId);

        if (target is null)
        {

            return;

        }

        target.BringIntoView();

    }

    private void ScheduleRender(string? markdown)
    {

        _debounceCts?.Cancel();

        _debounceCts?.Dispose();

        CancellationTokenSource cts = new();

        _debounceCts = cts;

        _ = DebounceAndRenderAsync(markdown, cts.Token);

    }

    private async Task DebounceAndRenderAsync(string? markdown, CancellationToken cancellationToken)
    {

        try
        {

            // Debounce off the UI thread so typing does not pin the dispatcher for 250ms.
            await Task.Delay(DebounceInterval, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return;

        }

        if (cancellationToken.IsCancellationRequested)
        {

            return;

        }

        int generation = _renderGeneration.Begin();

        // Assign the new CTS to the field before touching the previous one: a superseding call
        // reads _renderCts as "the one to cancel and dispose", so the field must already point at
        // ours, never at a CTS some other overlapping call might still be about to read from.
        CancellationTokenSource? previousRenderCts = _renderCts;

        CancellationTokenSource renderCts = new();

        _renderCts = renderCts;

        previousRenderCts?.Cancel();

        previousRenderCts?.Dispose();

        // A still-overlapping superseding call can dispose renderCts between the field assignment
        // above and this read; null means exactly that — treat it like losing the generation /
        // cancellation race checked below, not as a fault.
        CancellationTokenSource? linkedCtsOrNull = TryCreateLinkedCts(cancellationToken, renderCts.Token);

        if (linkedCtsOrNull is null)
        {

            return;

        }

        using CancellationTokenSource linkedCts = linkedCtsOrNull;

        IlluminationPreparedMarkdown prepared;

        try
        {

            // Sanitize + Markdig parse + source-line map: pure work, no Avalonia objects.
            prepared = await Task.Run(
                    () => IlluminationMarkdownPrepare.Prepare(markdown),
                    linkedCts.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return;

        }

        if (!_renderGeneration.IsCurrent(generation) || renderCts.IsCancellationRequested)
        {

            return;

        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {

            if (!_renderGeneration.IsCurrent(generation) || renderCts.IsCancellationRequested)
            {

                return;

            }

            // Unguarded, this body's exceptions (a highlighter/renderer fault on unusual input, or
            // an image-resolution fault) would fault this InvokeAsync delegate's Task, which faults
            // the fire-and-forget DebounceAndRenderAsync task, unobserved — PreviewHost.Content is
            // never reassigned and the preview silently keeps showing the previous document with no
            // sign anything went wrong.
            try
            {

                TruncationNotice.Text = TruncationNoticeDefaultText;

                TruncationNotice.IsVisible = prepared.Truncated;

                // Precomputed map is available immediately; renderer anchors replace it after control build.
                _lineMapper = new MarkdownSourceLineMapper(prepared.Anchors);

                IlluminationImageContext imageContext = new()
                {

                    LoadRemoteImages = LoadRemoteImages,

                    WorkspaceId = WorkspaceId,

                    RelativePath = RelativePath,

                    BaseRelativeDirectory = BaseRelativeDirectory,

                };

                MarkdigAstAvaloniaRenderer renderer = new(_highlighter, _imageResolver, _hyperlinkCommand);

                renderer.GoToSourceRequested += (_, line) =>
                {

                    if (!SyncScrollEnabled)
                    {

                        return;

                    }

                    GoToSourceRequested?.Invoke(this, line);

                };

                Control content = renderer.Render(prepared.Document, imageContext, renderCts.Token);

                // Stale-render guard: if A started, B superseded it, and A finishes last, A must not publish.
                if (!_renderGeneration.IsCurrent(generation) || renderCts.IsCancellationRequested)
                {

                    return;

                }

                _activeRenderer = renderer;

                _lineMapper = new MarkdownSourceLineMapper(renderer.Anchors);

                PreviewHost.Content = content;

            }
            catch (OperationCanceledException)
            {

                // A newer render superseded this one during control construction.
            }
            catch (Exception)
            {

                if (_renderGeneration.IsCurrent(generation))
                {

                    TruncationNotice.Text = RenderFailedNoticeText;

                    TruncationNotice.IsVisible = true;

                }

            }

        });

    }

    private static CancellationTokenSource? TryCreateLinkedCts(CancellationToken a, CancellationToken b)
    {

        try
        {

            return CancellationTokenSource.CreateLinkedTokenSource(a, b);

        }
        catch (ObjectDisposedException)
        {

            return null;

        }

    }

    private static Control? FindByBlockId(Control root, string blockId)
    {

        if (string.Equals(root.GetValue(MarkdigAstAvaloniaRenderer.BlockIdProperty), blockId, StringComparison.Ordinal))
        {

            return root;

        }

        if (root is Panel panel)
        {

            foreach (Control child in panel.Children.OfType<Control>())
            {

                Control? found = FindByBlockId(child, blockId);

                if (found is not null)
                {

                    return found;

                }

            }

        }
        else if (root is ContentControl { Content: Control contentChild })
        {

            return FindByBlockId(contentChild, blockId);

        }
        else if (root is Decorator { Child: Control decorated })
        {

            return FindByBlockId(decorated, blockId);

        }
        else if (root is Border { Child: Control borderChild })
        {

            return FindByBlockId(borderChild, blockId);

        }

        return null;

    }

}
