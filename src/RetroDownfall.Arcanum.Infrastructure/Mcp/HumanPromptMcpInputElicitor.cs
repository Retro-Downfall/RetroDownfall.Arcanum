using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Default <see cref="IMcpInputElicitor"/>: bridges each MRTR input request into the shared
/// <see cref="IHumanPromptRegistry"/> (the same correlation surface as the in-process
/// <c>ask_human</c> tool), so an HTTP server's input requests are answered by the operator over
/// the existing CLI / HTTP human-prompt channel. Each request's <see cref="McpInputRequest.Id"/> is
/// used as the prompt id.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin adapter over IHumanPromptRegistry; MRTR round-trip logic is covered via McpHttpClientTests with a fake elicitor.
internal sealed class HumanPromptMcpInputElicitor(IHumanPromptRegistry humanPromptRegistry) : IMcpInputElicitor
{

    public async Task<IReadOnlyList<McpInputResponse>> ElicitAsync(
        IReadOnlyList<McpInputRequest> requests,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(requests);

        List<McpInputResponse> responses = new(requests.Count);

        foreach (McpInputRequest request in requests)
        {

            string value = await humanPromptRegistry
                .WaitForResponseAsync(request.Id, cancellationToken)
                .ConfigureAwait(false);

            responses.Add(new McpInputResponse { Id = request.Id, Value = value });

        }

        return responses;

    }

}
