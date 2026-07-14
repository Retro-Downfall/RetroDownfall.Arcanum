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

    private static readonly MarkdownImageCache SharedImageCache = new();

    private readonly IlluminationHyperlinkCommand _hyperlinkCommand = new();

    private readonly IMarkdownImageResolver _imageResolver =
        new MarkdownImageResolver(new RemoteMarkdownImageLoader(), SharedImageCache);

    private readonly IMarkdownCodeHighlighter _highlighter = new ColorCodeMarkdownCodeHighlighter();

    private CancellationTokenSource? _debounceCts;

    private CancellationTokenSource? _renderCts;

    private int _renderGeneration;

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

            await Task.Delay(DebounceInterval, cancellationToken).ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            return;

        }

        if (cancellationToken.IsCancellationRequested)
        {

            return;

        }

        string sanitized = MarkdownSafetySanitizer.Sanitize(markdown, out bool truncated);

        int generation = Interlocked.Increment(ref _renderGeneration);

        _renderCts?.Cancel();

        _renderCts?.Dispose();

        CancellationTokenSource renderCts = new();

        _renderCts = renderCts;

        IlluminationImageContext imageContext = new()
        {

            LoadRemoteImages = LoadRemoteImages,

            WorkspaceId = WorkspaceId,

            RelativePath = RelativePath,

            BaseRelativeDirectory = BaseRelativeDirectory,

        };

        await Dispatcher.UIThread.InvokeAsync(() =>
        {

            if (renderCts.IsCancellationRequested || generation != Volatile.Read(ref _renderGeneration))
            {

                return;

            }

            TruncationNotice.IsVisible = truncated;

            MarkdigAstAvaloniaRenderer renderer = new(_highlighter, _imageResolver, _hyperlinkCommand);

            renderer.GoToSourceRequested += (_, line) =>
            {

                if (!SyncScrollEnabled)
                {

                    return;

                }

                GoToSourceRequested?.Invoke(this, line);

            };

            Control content = renderer.Render(sanitized, imageContext, renderCts.Token);

            if (renderCts.IsCancellationRequested || generation != Volatile.Read(ref _renderGeneration))
            {

                return;

            }

            _activeRenderer = renderer;

            _lineMapper = new MarkdownSourceLineMapper(renderer.Anchors);

            PreviewHost.Content = content;

        });

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
