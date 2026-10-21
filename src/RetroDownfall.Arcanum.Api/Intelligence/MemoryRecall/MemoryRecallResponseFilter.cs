using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryRecall;

internal sealed class MemoryRecallResponseFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        object? result = await next(context).ConfigureAwait(false);

        return result is IResult response
            && context.HttpContext.RequestServices.GetService<MemoryRecallResponseLifetime>() is { } lifetime
                ? lifetime.Wrap(response)
                : result;
    }
}
