using System.Threading;
using Avalonia.Media;
using ColorCode;
using ColorCode.Common;
using ColorCode.Compilation;
using ColorCode.Parsing;

namespace RetroDownfall.TheForge.Ux.Markdown;

public interface IMarkdownCodeHighlighter
{

    IReadOnlyList<HighlightedSpan> Highlight(string code, string? languageInfo);

}

public sealed record HighlightedSpan(string Text, string? ResourceBrushKey);

public sealed class ColorCodeMarkdownCodeHighlighter : IMarkdownCodeHighlighter
{

    private readonly LanguageParser _parser = CreateParser();

    public IReadOnlyList<HighlightedSpan> Highlight(string code, string? languageInfo)
    {

        if (string.IsNullOrEmpty(code))
        {

            return [];

        }

        ILanguage? language = ResolveLanguage(languageInfo);

        if (language is null)
        {

            return [new HighlightedSpan(code, null)];

        }

        List<HighlightedSpan> spans = [];

        try
        {

            _parser.Parse(code, language, (parsed, scopes) =>
            {

                if (scopes is null || scopes.Count == 0)
                {

                    spans.Add(new HighlightedSpan(parsed, null));

                    return;

                }

                // ColorCode yields the full segment with nested scopes; emit flat styled runs.
                string? brush = MapScopeBrush(scopes[0].Name);

                spans.Add(new HighlightedSpan(parsed, brush));

            });

        }
        catch
        {

            return [new HighlightedSpan(code, null)];

        }

        return spans.Count == 0 ? [new HighlightedSpan(code, null)] : spans;

    }

    public static string? NormalizeLanguageId(string? languageInfo)
    {

        if (string.IsNullOrWhiteSpace(languageInfo))
        {

            return null;

        }

        string id = languageInfo.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        return id switch
        {
            "cs" or "csharp" or "c#" => "csharp",
            "js" or "javascript" => "javascript",
            "ts" or "typescript" => "typescript",
            "ps" or "ps1" or "powershell" => "powershell",
            "py" or "python" => "python",
            "yml" or "yaml" => "xml",
            "sh" or "bash" or "shell" or "zsh" => "powershell",
            "htm" or "html" => "html",
            "c" or "cpp" or "c++" or "h" or "hpp" => "cpp",
            "fs" or "fsharp" => "fsharp",
            "vb" or "vbnet" => "vbnet",
            "md" or "markdown" => "markdown",
            "mermaid" => "markdown",
            _ => id,
        };

    }

    private static LanguageParser CreateParser()
    {

        Dictionary<string, ILanguage> languages = Languages.All.ToDictionary(
            static language => language.Id,
            StringComparer.OrdinalIgnoreCase);

        LanguageRepository repository = new(languages);

        LanguageCompiler compiler = new(new Dictionary<string, CompiledLanguage>(), new ReaderWriterLockSlim());

        return new LanguageParser(compiler, repository);

    }

    private static ILanguage? ResolveLanguage(string? languageInfo)
    {

        string? id = NormalizeLanguageId(languageInfo);

        if (id is null)
        {

            return null;

        }

        try
        {

            return Languages.FindById(id)
                ?? id switch
                {
                    "csharp" => Languages.CSharp,
                    "javascript" => Languages.JavaScript,
                    "typescript" => Languages.Typescript,
                    "powershell" => Languages.PowerShell,
                    "python" => Languages.Python,
                    "xml" => Languages.Xml,
                    "html" => Languages.Html,
                    "cpp" => Languages.Cpp,
                    "sql" => Languages.Sql,
                    "css" => Languages.Css,
                    "json" => Languages.FindById("json"),
                    "fsharp" => Languages.FSharp,
                    "vbnet" => Languages.VbDotNet,
                    "java" => Languages.Java,
                    "php" => Languages.Php,
                    "markdown" => Languages.Markdown,
                    _ => null,
                };

        }
        catch
        {

            return null;

        }

    }

    private static string? MapScopeBrush(string? scopeName)
    {

        if (string.IsNullOrEmpty(scopeName))
        {

            return null;

        }

        if (scopeName.Contains("Keyword", StringComparison.OrdinalIgnoreCase)
            || scopeName.Contains("Control", StringComparison.OrdinalIgnoreCase))
        {

            return "ForgeCodeKeywordBrush";

        }

        if (scopeName.Contains("String", StringComparison.OrdinalIgnoreCase)
            || scopeName.Contains("XmlDoc", StringComparison.OrdinalIgnoreCase))
        {

            return "ForgeCodeStringBrush";

        }

        if (scopeName.Contains("Comment", StringComparison.OrdinalIgnoreCase))
        {

            return "ForgeCodeCommentBrush";

        }

        if (scopeName.Contains("Number", StringComparison.OrdinalIgnoreCase))
        {

            return "ForgeCodeNumberBrush";

        }

        if (scopeName.Contains("Type", StringComparison.OrdinalIgnoreCase)
            || scopeName.Contains("Class", StringComparison.OrdinalIgnoreCase))
        {

            return "ForgeCodeTypeBrush";

        }

        return null;

    }

}
