using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The text of the first physical send of the lease-parameter fixture. Fixtures that need one more effect inside the first send
    // append it after this anchor.
    private const string LeaseParameterFirstSend = "System.IO.File.Delete(\"lease-parameter-first\");";

    // The Lease Spine in one compilation. The hosted worker mints the kind-17 lease, retains it with await using, and hands the
    // retained local by value to the admitted sweep port; the sweep passes its lease formal to each of two sends, and each send
    // begins its own external-effect group on that formal. Nothing stores or binds the lease. Where the production shape is spread
    // over the Infrastructure and Api compilations (LeaseSpine), this one is the same chain in one tree, so a fixture that needs
    // the compiler's view of a record or a conversion in the sends can use a single compilation.
    //
    // The shape argument is the one thing that varies:
    //   lease-parameter  the shipped shape: every send uses the lease formal.
    //   holder-read      the first send reads a holder's lease property instead of using its formal (the Ambient Holder).
    //   holder-group     the first send begins its group on that holder lease.
    // The holder is the deleted LongRestWorkAdmission, kept here only as the hazard the Ambient Holder Refusal exists to refuse.
    private static string OrdinaryLeaseParameterFixture(string shape = "lease-parameter")
    {
        Assert.Contains(shape, new[] { "lease-parameter", "holder-read", "holder-group" });

        bool holder = shape != "lease-parameter";

        string constructor = holder ? "(LongRestWorkAdmission holder)" : "";

        string registration = holder ? "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>(); " : "";

        string firstLease = shape switch
        {
            "holder-read" => "IGrimoireWorkLease ambient = holder.Lease;",
            "holder-group" => "IGrimoireWorkLease ambient = holder.Lease;",
            _ => "",
        };

        string firstGroup = shape switch
        {
            "holder-read" => "",
            "holder-group" => "if (!ambient.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;",
            _ => "if (!admission.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;",
        };

        string holderType = holder ? """

            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                using RetroDownfall.Arcanum.Infrastructure.Data;

                internal sealed class LongRestWorkAdmission
                {
                    private IGrimoireWorkLease? current;

                    internal IGrimoireWorkLease Lease => current ?? throw new InvalidOperationException();

                    internal IDisposable Bind(IGrimoireWorkLease lease)
                    {
                        current = lease;

                        return new Binding();
                    }

                    private sealed class Binding : IDisposable
                    {
                        public void Dispose()
                        {
                        }
                    }
                }
            }
            """ : "";

        return $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            public class Worker(IServiceScopeFactory scopeFactory) : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    await RunOneAsync(Guid.Empty, token).ConfigureAwait(false);
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;

                private async Task RunOneAsync(Guid operation, CancellationToken ct)
                {
                    RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;

                    if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.LongRestSweep,
                        out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease admitted)) return;

                    await using RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted;

                    await using var scope = scopeFactory.CreateAsyncScope();

                    RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep sweep =
                        scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep>();

                    await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false);
                }
            }

            public static class Composition
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddHostedService<Worker>();

                    {{registration}}services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep, RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator>();
                }
            }

            namespace RetroDownfall.Arcanum.Api
            {
                public static class ApiBootstrapper
                {
                    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services)
                    {
                        global::Composition.Configure(services);

                        return services;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRestSweep = 17 }

                public interface IGrimoireWorkLease : IDisposable, IAsyncDisposable
                {
                    bool TryBeginExternalEffectGroup(out IDisposable group);
                }

                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease);
                }
            }
            {{holderType}}

            namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest
            {
                using RetroDownfall.Arcanum.Infrastructure.Data;

                using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

                internal interface ILongRestAdmittedSweep
                {
                    Task ResumeAdmittedAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct);
                }

                internal sealed class LongRestSweepCoordinator{{constructor}} : ILongRestAdmittedSweep
                {
                    public async Task ResumeAdmittedAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        await SendFirstAsync(admission, ct).ConfigureAwait(false);

                        await SendSecondAsync(admission, ct).ConfigureAwait(false);
                    }

                    private async Task SendFirstAsync(IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        {{firstLease}}

                        {{firstGroup}}

                        await Task.CompletedTask;

                        System.IO.File.Delete("lease-parameter-first");
                    }

                    private async Task SendSecondAsync(IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        if (!admission.TryBeginExternalEffectGroup(out IDisposable group)) return;

                        using var owned = group;

                        await Task.CompletedTask;

                        System.IO.File.Delete("lease-parameter-second");
                    }
                }
            }
            """;
    }
}
