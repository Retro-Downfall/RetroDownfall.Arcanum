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
    // The remaining rows are the same holder read, with a holder that came by its lease some other way. None of them is bound by a
    // binder named Bind, because the refusal is of the read and not of the holder's name:
    //   holder-read-renamed-binder      the holder's binder is named Attach instead of Bind (and is never called).
    //   holder-read-without-binder      the holder declares no binder at all.
    //   holder-read-property-setter     the holder's lease property has a setter, which is how a container or an initializer would hand
    //                                   the lease in. Nothing traversed calls it, because a call that stores the lease is itself the
    //                                   escape that ends the admission, and these rows isolate the read.
    //   holder-read-constructor-captured  the holder takes the lease in its constructor, and the first send builds one from its formal.
    //   holder-read-field               the holder keeps the lease in a public field.
    //   holder-read-conditional-access  the first send reads the holder's lease property through a conditional access.
    // The holder is the deleted LongRestWorkAdmission, kept here only as the hazard the Ambient Holder Refusal exists to refuse.
    private static readonly string[] OrdinaryLeaseParameterShapes =
    [
        "lease-parameter", "holder-read", "holder-group", "holder-read-renamed-binder", "holder-read-without-binder",
        "holder-read-property-setter", "holder-read-constructor-captured", "holder-read-field", "holder-read-conditional-access",
    ];

    private static string OrdinaryLeaseParameterFixture(string shape = "lease-parameter")
    {
        Assert.Contains(shape, OrdinaryLeaseParameterShapes);

        bool holder = shape != "lease-parameter";

        // The sweep is handed the holder by its constructor, except where the first send builds its own from the lease formal.
        bool injected = holder && shape != "holder-read-constructor-captured";

        string binder = shape == "holder-read-renamed-binder" ? "Attach" : "Bind";

        string holderMembers = shape switch
        {
            "holder-read-without-binder" => """
                private IGrimoireWorkLease? current;

                    internal IGrimoireWorkLease Lease => current ?? throw new InvalidOperationException();
                """,
            "holder-read-property-setter" => """
                internal IGrimoireWorkLease Lease { get; set; } = null!;
                """,
            "holder-read-constructor-captured" => """
                private readonly IGrimoireWorkLease captured;

                    internal LongRestWorkAdmission(IGrimoireWorkLease lease)
                    {
                        captured = lease;
                    }

                    internal IGrimoireWorkLease Lease => captured;
                """,
            "holder-read-field" => """
                internal IGrimoireWorkLease Lease = null!;
                """,
            _ => $$"""
                private IGrimoireWorkLease? current;

                    internal IGrimoireWorkLease Lease => current ?? throw new InvalidOperationException();

                    internal IDisposable {{binder}}(IGrimoireWorkLease lease)
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
                """,
        };

        string constructor = injected ? "(LongRestWorkAdmission holder)" : "";

        string registration = injected ? "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>(); " : "";

        string firstLease = shape switch
        {
            "lease-parameter" => "",
            "holder-read-constructor-captured" => "LongRestWorkAdmission holder = new(admission); IGrimoireWorkLease ambient = holder.Lease;",
            "holder-read-conditional-access" => "IGrimoireWorkLease? ambient = holder?.Lease;",
            _ => "IGrimoireWorkLease ambient = holder.Lease;",
        };

        string firstGroup = shape switch
        {
            "holder-group" => "if (!ambient.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;",
            "lease-parameter" => "if (!admission.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;",
            _ => "",
        };

        string holderType = holder ? $$"""

            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                using RetroDownfall.Arcanum.Infrastructure.Data;

                internal sealed class LongRestWorkAdmission
                {
                    {{holderMembers}}
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
