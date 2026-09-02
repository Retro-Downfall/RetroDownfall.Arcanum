using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;
using SysEnv = System.Environment;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class HostProcessToolPolicyTests
{

    [Fact]
    public void IsHostProcessTool_RecognizesExecuteCommandAndRunSpellScript()
    {
        Assert.True(HostProcessToolPolicy.IsHostProcessTool("execute_command"));
        Assert.True(HostProcessToolPolicy.IsHostProcessTool("RUN_SPELL_SCRIPT"));
        Assert.False(HostProcessToolPolicy.IsHostProcessTool("read_file_chunk"));
        Assert.False(HostProcessToolPolicy.IsHostProcessTool(null));
    }

    [Fact]
    public void AreAllowed_LocalEdition_DeniedEvenIfEnvSet()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);
            string? previousEdition = SysEnv.GetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");
                SysEnv.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, null);

                Assert.False(HostProcessToolPolicy.AreAllowed(ArcanumEdition.Local));
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
                SysEnv.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, previousEdition);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void AreAllowed_DevelopmentWithoutEnv_Denied()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, null);

                Assert.False(HostProcessToolPolicy.AreAllowed(ArcanumEdition.Development));
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void AreAllowed_DevelopmentWithEnv_AllowedAndHealthDegraded()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");

                Assert.True(HostProcessToolPolicy.AreAllowed(ArcanumEdition.Development));

                HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Development);

                Assert.True(status.Allowed);
                Assert.True(status.IsHealthDegraded);
                Assert.True(status.EscapeHatchEnvSet);
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    /// <summary>
    /// The arm where the two inputs say yes and the gate has already said no.
    /// </summary>
    /// <remarks>
    /// Development plus <c>ARCANUM_ALLOW_HOST_PROCESS_TOOLS</c> is what every other Resolve arm turns
    /// on, and on an installation with no completed host-process-tools transition it is also exactly
    /// what the startup gate blocks - so this state is the common one for an operator who read the
    /// old denial and did what it said. Health has to report it as the gate's refusal: the tools are
    /// not running, so the process is not Degraded for having them, and an operator told otherwise
    /// goes looking for a hatch that is shut. The decision comes from a real
    /// <c>ClassifyAndPublishAsync</c> run rather than a hand-built policy, so a gate that stopped
    /// blocking this state fails here instead of quietly agreeing with the status.
    /// </remarks>
    [Fact]
    public async Task Resolve_DevelopmentWithEnvButRefusedByTheStartupGate_ReportsTheRefusal()
    {
        using HostProcessToolsEscapeHatchScope scope = new();

        FakeHostProcessToolsEnvironmentProbe environment = new()
        {
            Edition = ArcanumEdition.Development,
            EscapeHatchOptIn = true,
        };

        HostProcessToolsRuntimePolicy published = new();

        HostProcessToolsStartupGate gate = new(
            new FakeHostProcessToolsMarkerStore(),
            new FakeHostProcessToolsAuthorityStore(),
            environment,
            new HostProcessToolsMarkerPairJoiner(),
            published);

        Result<HostProcessToolsStartupDecision> classified =
            await gate.ClassifyAndPublishAsync(CancellationToken.None);

        // The premise, measured: a clean installation started with the hatch armed is refused, and
        // the refusal is published rather than left for each call site to re-derive.
        Assert.True(classified.IsFailure);

        Assert.True(published.IsPublished);

        Assert.Equal(HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition, published.Blocker);

        try
        {
            HostProcessToolPolicy.BindStartupDecision(published);

            HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Development);

            // The scope set both inputs, so only the gate's decision can be subtracting here.
            Assert.True(status.EscapeHatchEnvSet);

            Assert.False(status.Allowed);

            Assert.False(status.IsHealthDegraded);

            Assert.Contains("startup gate refused", status.PublicMessage, StringComparison.Ordinal);

            Assert.False(HostProcessToolPolicy.AreAllowed(ArcanumEdition.Development));
        }
        finally
        {
            // Process-wide: a test that leaves this bound hands every later test a refusal it never
            // asked for.
            HostProcessToolPolicy.SetStartupDecisionForTests(null);
        }
    }

    [Fact]
    public void IsHostProcessTool_RejectsEmptyAndWhitespace()
    {
        Assert.False(HostProcessToolPolicy.IsHostProcessTool(""));
        Assert.False(HostProcessToolPolicy.IsHostProcessTool("   "));
        Assert.False(HostProcessToolPolicy.IsHostProcessTool("execute_command_extra"));
    }

    [Fact]
    public void Resolve_LocalEdition_ReportsDisabled()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, null);

                HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Local);

                Assert.False(status.Allowed);
                Assert.False(status.IsHealthDegraded);
                Assert.False(status.EscapeHatchEnvSet);
                Assert.Contains("Local edition", status.PublicMessage, StringComparison.Ordinal);
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void Resolve_LocalEditionWithEscapeHatch_ReportsDeniedButFlagged()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");

                HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Local);

                Assert.Equal(ArcanumEdition.Local, status.Edition);
                Assert.False(status.Allowed);
                Assert.False(status.IsHealthDegraded);
                Assert.True(status.EscapeHatchEnvSet);
                Assert.Contains("Local edition", status.PublicMessage, StringComparison.Ordinal);
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void Resolve_DevelopmentWithoutEnv_ReportsWaitingForEscapeHatch()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, null);

                HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Development);

                Assert.False(status.Allowed);
                Assert.False(status.IsHealthDegraded);
                Assert.False(status.EscapeHatchEnvSet);
                Assert.Contains("remain off until", status.PublicMessage, StringComparison.Ordinal);
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void Resolve_DevelopmentWithEnv_ReportsUnsafeEscapeHatch()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previousAllow = SysEnv.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");

                HostProcessToolPolicyStatus status = HostProcessToolPolicy.Resolve(ArcanumEdition.Development);

                Assert.True(status.Allowed);
                Assert.True(status.IsHealthDegraded);
                Assert.Contains("unsafe escape hatch", status.PublicMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

    [Fact]
    public void ResolveEdition_EnvOverridesConfig()
    {
        HostProcessToolsEscapeHatchScope.Gate.Wait();

        try
        {
            string? previous = SysEnv.GetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar);

            try
            {
                SysEnv.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, "development");

                Assert.Equal(ArcanumEdition.Development, ArcanumEnvironment.ResolveEdition(ArcanumEdition.Local));

                SysEnv.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, "local");

                Assert.Equal(ArcanumEdition.Local, ArcanumEnvironment.ResolveEdition(ArcanumEdition.Development));
            }
            finally
            {
                SysEnv.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, previous);
            }
        }
        finally
        {
            _ = HostProcessToolsEscapeHatchScope.Gate.Release();
        }
    }

}
