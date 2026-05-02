using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class ArcanumLocalTimeTool : AIFunction
{
    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }

        """);

    public override string Name => "GetLocalSystemTime";

    public override string Description => "Gets the current local system time.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        _ = arguments;

        _ = cancellationToken;

        return new ValueTask<object?>(DateTime.Now.ToString("O"));
    }
}
