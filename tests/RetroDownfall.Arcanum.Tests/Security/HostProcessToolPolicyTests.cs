using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;
using SysEnv = System.Environment;

namespace RetroDownfall.Arcanum.Tests.Security;

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
