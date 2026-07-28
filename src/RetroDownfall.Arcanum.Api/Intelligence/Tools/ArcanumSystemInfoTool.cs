using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class ArcanumSystemInfoTool : AIFunction
{
    public const string ToolName =
        ArcanumBuiltInToolNames.GetArcanumSystemInfo;

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }

        """);

    public override string Name => ToolName;

    public override string Description => "Returns the host operating system, CPU architecture, and .NET runtime version for the API process.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        _ = arguments;

        _ = cancellationToken;

        return new ValueTask<object?>(
            $"OS: {RuntimeInformation.OSDescription} | Architecture: {RuntimeInformation.OSArchitecture} | .NET Runtime: {Environment.Version}");
    }
}
