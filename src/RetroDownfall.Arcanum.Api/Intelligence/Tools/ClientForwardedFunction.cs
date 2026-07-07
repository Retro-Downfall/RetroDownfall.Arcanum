using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// A surrogate <see cref="AIFunction"/> that carries a client-supplied OpenAI tool definition
/// through to the resolved provider. The function is never executed by Arcanum; when the
/// provider emits a <see cref="FunctionCallContent"/> for it, the hub breaks the tool loop and
/// surfaces the call to the client.
/// </summary>
public sealed class ClientForwardedFunction : AIFunction
{

    private static readonly JsonDocument EmptySchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }

        """);

    private readonly OpenAiFunctionDefinition _definition;

    public ClientForwardedFunction(OpenAiFunctionDefinition definition)
    {

        _definition = definition;

    }

    public override string Name => _definition.Name;

    public override string Description => _definition.Description ?? string.Empty;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties
    {

        get
        {

            if (_definition.Strict is true)
            {

                return new Dictionary<string, object?> { ["strict"] = true };

            }

            return base.AdditionalProperties;

        }

    }

    public override JsonElement JsonSchema
    {

        get
        {

            if (_definition.Parameters is null
                || _definition.Parameters.Value.ValueKind is JsonValueKind.Null
                || _definition.Parameters.Value.ValueKind is JsonValueKind.Undefined)
            {

                return EmptySchemaDocument.RootElement;

            }

            return _definition.Parameters.Value.Clone();

        }

    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {

        _ = arguments;

        _ = cancellationToken;

        return new ValueTask<object?>($"Client tool '{Name}' is forwarded to the provider and not executed by Arcanum.");

    }

}
