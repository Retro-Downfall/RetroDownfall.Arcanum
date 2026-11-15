using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Only the stock publication contract used by these exact consumers. This
        // does not certify custom builders or replace their normal effect traversal.
        private static bool HasStockAsyncPublication(AuthoredMember member)
        {
            if (!member.Symbol.IsAsync
                || member.Symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "AsyncMethodBuilderAttribute")) return false;

            (string Metadata, AssemblyName Assembly)? builder =
                IsExactFrameworkType(member.Symbol.ReturnType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                    ? ("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder).Assembly.GetName())
                    : IsExactFrameworkType(member.Symbol.ReturnType, "System.Threading.Tasks.Task`1", typeof(Task<>).Assembly.GetName())
                        ? ("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1", typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<>).Assembly.GetName())
                        : IsExactFrameworkType(member.Symbol.ReturnType, "System.Threading.Tasks.ValueTask", typeof(ValueTask).Assembly.GetName())
                            ? ("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder", typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder).Assembly.GetName())
                            : IsExactFrameworkType(member.Symbol.ReturnType, "System.Threading.Tasks.ValueTask`1", typeof(ValueTask<>).Assembly.GetName())
                                ? ("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1", typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<>).Assembly.GetName())
                                : null;

            return builder is { } stock && IsExactFrameworkType(member.Model.Compilation.GetTypeByMetadataName(stock.Metadata), stock.Metadata, stock.Assembly);
        }
    }
}
