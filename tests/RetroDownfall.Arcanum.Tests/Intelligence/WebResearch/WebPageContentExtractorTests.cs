using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Tests.Intelligence.WebResearch;

public sealed class WebPageContentExtractorTests
{
    private const int MaxContentBytes = 50_000;

    private const int MaxLinks = 10;

    private const int MaxLinkUrlChars = 2_048;

    [Fact]
    public void Extract_returns_bounded_markdown_for_deeply_nested_markup()
    {
        string html = "<html><body>"
            + string.Concat(Enumerable.Repeat("<div>", 50_000))
            + "deep"
            + string.Concat(Enumerable.Repeat("</div>", 50_000))
            + "</body></html>";

        WebPageExtractionResult result = new WebPageContentExtractor().Extract(
            html,
            new Uri("https://example.test/deep"),
            MaxContentBytes,
            MaxLinks,
            MaxLinkUrlChars);

        Assert.True(result.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(result.Markdown) <= MaxContentBytes);
        Assert.Empty(result.Links);
    }

    [Fact]
    public void Extract_bounds_the_working_set_for_a_link_dense_page_under_a_long_base_uri()
    {
        Uri baseUri = new("https://example.test/" + new string('a', 3_800) + "/p");
        StringBuilder body = new("<html><body><main>");

        for (int index = 0; index < 2_000; index++)
        {
            body.Append("<a href=\"").Append(index).Append("\">x</a>");
        }

        body.Append("</main></body></html>");

        string html = body.ToString();
        WebPageContentExtractor extractor = new();

        _ = extractor.Extract(
            "<html><body><main><p>warm</p></main></body></html>",
            baseUri,
            MaxContentBytes,
            MaxLinks,
            MaxLinkUrlChars);

        long before = GC.GetAllocatedBytesForCurrentThread();
        WebPageExtractionResult result = extractor.Extract(
            html,
            baseUri,
            MaxContentBytes,
            MaxLinks,
            MaxLinkUrlChars);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(Encoding.UTF8.GetByteCount(result.Markdown) <= MaxContentBytes);
        Assert.True(result.Truncated);
        Assert.True(result.Links.Length <= MaxLinks);
        Assert.True(
            allocated < 24_000_000,
            $"Extracting a {html.Length}-char page under a {baseUri.AbsoluteUri.Length}-char base URI allocated {allocated} bytes.");
    }

    [Fact]
    public void Extract_renders_ordinary_markup_and_resolves_relative_links()
    {
        WebPageExtractionResult result = new WebPageContentExtractor().Extract(
            """
            <html>
              <head><title>Ordinary</title></head>
              <body>
                <main>
                  <h2>Heading</h2>
                  <p>Body <em>text</em>.</p>
                  <a href="/one">One</a>
                  <a href="/one#frag">One again</a>
                </main>
              </body>
            </html>
            """,
            new Uri("https://example.test/start"),
            MaxContentBytes,
            MaxLinks,
            MaxLinkUrlChars);

        Assert.Equal("Ordinary", result.Title);
        Assert.Contains("## Heading", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("*text*", result.Markdown, StringComparison.Ordinal);
        Assert.Equal("https://example.test/one", Assert.Single(result.Links).Url);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.OmittedLinkCount);
    }
}
