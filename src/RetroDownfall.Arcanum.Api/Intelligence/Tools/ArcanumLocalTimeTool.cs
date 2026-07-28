using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class ArcanumLocalTimeTool : AIFunction
{
    public const string ToolName =
        ArcanumBuiltInToolNames.GetLocalSystemTime;

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }

        """);

    public override string Name => ToolName;

    public override string Description => "Gets the current local system time.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        _ = arguments;

        _ = cancellationToken;

        return new ValueTask<object?>(DateTime.Now.ToString("O"));
    }
}
