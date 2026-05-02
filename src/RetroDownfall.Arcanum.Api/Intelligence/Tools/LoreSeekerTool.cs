using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class LoreSeekerTool : AIFunction
{

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(

        """

        {

          "type": "object",

          "properties": {

            "relativePath": {

              "type": "string",

              "description": "File path relative to the workspace root."

            }

          },

          "required": ["relativePath"],

          "additionalProperties": false

        }

        """);

    private readonly string? _workspaceRoot;

    private readonly string? _workspaceConfigurationError;

    public LoreSeekerTool(string workingDirectory)
    {

        if (!ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? root, out string? err))
        {

            _workspaceRoot = null;

            _workspaceConfigurationError = err;

            return;

        }

        _workspaceRoot = root;

        _workspaceConfigurationError = null;

    }

    public override string Name => "seek_workspace_lore";

    public override string Description =>

        "Reads the contents of a text or markdown file within the current workspace. Use this to read reference files, lore, or code.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {

        if (_workspaceConfigurationError is not null)
        {

            return _workspaceConfigurationError;

        }

        if (_workspaceRoot is null)
        {

            return "The workspace is not configured for file access on this request.";

        }

        if (!ToolHelpers.TryGetRequiredStringArgument(arguments, "relativePath", out string? relativePath, out string? argError))
        {

            return argError;

        }

        if (Path.IsPathRooted(relativePath))
        {

            return

                "The path must be relative to the workspace (absolute paths are not allowed). Please use a path like `README.md` or `docs/guide.md`.";

        }

        string combined;

        try
        {

            combined = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));

        }

        catch (Exception)
        {

            return "That file path could not be resolved. Please simplify the path and try again.";

        }

        if (!ToolHelpers.IsPathUnderWorkspace(_workspaceRoot, combined))
        {

            return

                "That path would leave the workspace sandbox, so it was not opened. Please stay within the project directory.";

        }

        if (!File.Exists(combined))
        {

            return

                $"There is no file at `{relativePath}` in the workspace (after resolving paths). If the name might be wrong, try listing the directory or asking the operator for the correct relative path.";

        }

        try
        {

            string text = await File.ReadAllTextAsync(combined, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            return text;

        }

        catch (UnauthorizedAccessException)
        {

            return "The file exists but could not be read due to permissions. The operator may need to adjust access rights.";

        }

        catch (IOException ex)
        {

            return $"The file could not be read: {ex.Message}";

        }

    }

}
