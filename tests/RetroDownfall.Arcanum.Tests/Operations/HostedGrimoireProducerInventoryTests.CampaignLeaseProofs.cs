using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("adapter", false)]
    [InlineData("unknown", true)]
    [InlineData("reassigned", true)]
    [InlineData("owned-http", false)]
    [InlineData("foreign-client", true)]
    [InlineData("mixed-client", true)]
    [InlineData("foreign-http", false)]
    [InlineData("converted-null-http", false)]
    public void CampaignLeaseCleanupRetainsGuardedFactoryAndReadonlyStorageProvenance(string shape, bool unresolved)
    {
        string initializer = shape == "unknown"
            ? "CampaignLeaseInput.Unknown()"
            : "Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(new OpenAI.Chat.ChatClient(\"fixture-model\", new System.ClientModel.ApiKeyCredential(\"fixture-key\"), new OpenAI.OpenAIClientOptions()))";

        string mutation = shape == "reassigned"
            ? "client = CampaignLeaseInput.Unknown();"
            : string.Empty;

        string ownedHttpClient = shape switch
        {
            "owned-http" => "CampaignLeaseInput.OwnedHttpClient()",
            "converted-null-http" => "(System.Net.Http.HttpClient?)(CustomTransport?)null",
            _ => "null",
        };

        string clientAccess = shape is "foreign-client" or "mixed-client" ? "CampaignLeaseInput.Other().Client" : "Client";

        string ownCleanup = shape == "mixed-client" ? "(Client as System.IDisposable)?.Dispose();" : string.Empty;

        string transportAccess = shape == "foreign-http" ? "CampaignLeaseInput.Other().ownedHttpClient" : "ownedHttpClient";

        string source = R2Source(R2Admission
            + "ICampaignLeaseFactory factory = new CampaignLeaseFactory(); "
            + "CampaignLeaseResult<CampaignLease?> resolved = await factory.ResolveAsync(token).ConfigureAwait(false); "
            + "if (resolved.IsFailure) return; if (resolved.Value is null) return; "
            + "using CampaignLease acquired = resolved.Value; System.IO.File.Exists(\"campaign-lease-control\");",
            $$"""
            internal sealed class CampaignLeaseResult<T>
            {
                private readonly T? value;

                private CampaignLeaseResult(T value) { this.value = value; IsSuccess = true; }

                internal bool IsSuccess { get; }

                internal bool IsFailure => !IsSuccess;

                internal T Value => IsSuccess ? value! : throw new System.InvalidOperationException();

                internal static CampaignLeaseResult<T> Success(T value) => new(value);
            }

            internal interface ICampaignLeaseFactory
            {
                System.Threading.Tasks.Task<CampaignLeaseResult<CampaignLease?>> ResolveAsync(System.Threading.CancellationToken token);
            }

            internal sealed class CampaignLeaseFactory : ICampaignLeaseFactory
            {
                public System.Threading.Tasks.Task<CampaignLeaseResult<CampaignLease?>> ResolveAsync(System.Threading.CancellationToken token)
                {
                    Microsoft.Extensions.AI.IChatClient client = {{initializer}};

                    {{mutation}}

                    return System.Threading.Tasks.Task.FromResult(CampaignLeaseResult<CampaignLease?>.Success(new(client, {{ownedHttpClient}})));
                }
            }

            internal sealed class CampaignLease : System.IDisposable
            {
                private readonly System.Net.Http.HttpClient? ownedHttpClient;

                internal CampaignLease(Microsoft.Extensions.AI.IChatClient client, System.Net.Http.HttpClient? ownedHttpClient)
                {
                    Client = client;

                    this.ownedHttpClient = ownedHttpClient;
                }

                internal Microsoft.Extensions.AI.IChatClient Client { get; }

                public void Dispose()
                {
                    {{ownCleanup}}

                    ({{clientAccess}} as System.IDisposable)?.Dispose();

                    {{transportAccess}}?.Dispose();
                }
            }

            internal static class CampaignLeaseInput
            {
                internal static extern Microsoft.Extensions.AI.IChatClient Unknown();

                internal static extern System.Net.Http.HttpClient OwnedHttpClient();

                internal static extern CampaignLease Other();
            }

            internal sealed class CustomTransport
            {
                public static explicit operator System.Net.Http.HttpClient?(CustomTransport? value) =>
                    CampaignLeaseInput.OwnedHttpClient();
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(unresolved, result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal)));

        Assert.Equal(shape is "owned-http" or "foreign-http" or "converted-null-http", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.HttpMessageInvoker.Dispose"));
    }
}
