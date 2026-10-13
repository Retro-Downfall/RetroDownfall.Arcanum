using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("readonly-gate")]
    [InlineData("forwarded-readonly-gate")]
    [InlineData("unknown-gate")]
    public void Returned_interface_lease_cleanup_follows_actual_gate_receiver_and_out_assignment(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        ReplaceOne("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", """
            public ValueTask DisposeAsync()
            {
                System.IO.File.Delete("actual-returned-lease-cleanup");

                return ValueTask.CompletedTask;
            }
            """);

        if (shape == "forwarded-readonly-gate")
        {
            ReplaceOne("gate = new ReadinessWorkGate();", "gate = new ForwardingReadinessWorkGate();");

            source += """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal sealed class ForwardingReadinessWorkGate : IGrimoireConnectionAdmissionGate
                    {
                        private readonly IGrimoireConnectionAdmissionGate inner = new ReadinessWorkGate();

                        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                            => inner.TryAcquireWorkLease(kind, out lease);
                    }
                }
                """;
        }
        else if (shape == "unknown-gate")
        {
            ReplaceOne("gate = new ReadinessWorkGate();", "gate = UnprovenReadinessGate.Current;");

            source += """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal static class UnprovenReadinessGate
                    {
                        internal static IGrimoireConnectionAdmissionGate Current = new ReadinessWorkGate();
                    }
                }
                """;
        }

        Assert.Empty(Compile(source).GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.DisposeAsync", StringComparison.Ordinal));

        HostedProducerSite[] cleanup = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease"
            && site.Member == "DisposeAsync" && site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.AuthorityOperationId == "Worker.StartAsync").ToArray();

        if (shape == "unknown-gate")
        {
            Assert.True(unresolved);

            Assert.Empty(cleanup);
        }
        else
        {
            Assert.False(unresolved);

            Assert.NotEmpty(cleanup);
        }
    }
}
