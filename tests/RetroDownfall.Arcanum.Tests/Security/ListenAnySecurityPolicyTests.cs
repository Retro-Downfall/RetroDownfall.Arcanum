using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class ListenAnySecurityPolicyTests : IDisposable
{
    private readonly string? _originalAck;

    private readonly string? _originalHostAny;

    private readonly string? _originalDotnetEnvironment;

    private readonly string? _originalAspNetCoreEnvironment;

    private readonly string? _originalTestHome;

    private readonly string _testHome;

  public ListenAnySecurityPolicyTests()
  {
    _testHome = Path.Combine(Path.GetTempPath(), "arcanum-listen-any-tests", Guid.NewGuid().ToString("N"));

    Directory.CreateDirectory(_testHome);

    _originalAck = global::System.Environment.GetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable);

    _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

    _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

    global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

    global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

    global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _testHome);

    global::System.Environment.SetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable, null);

    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);
  }

  public void Dispose()
  {
    global::System.Environment.SetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable, _originalAck);

    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    string marker = Path.Combine(ArcanumPaths.GrimoireDirectory, ".listen-any-acknowledged");

    if (File.Exists(marker))
    {
      File.Delete(marker);
    }

    global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

    global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

    global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

    if (Directory.Exists(_testHome))
    {
      Directory.Delete(_testHome, recursive: true);
    }
  }

  [Fact]
  public void RequiresInteractiveConfirmation_false_when_loopback_only()
  {
    Assert.False(ListenAnySecurityPolicy.RequiresInteractiveConfirmation(configListenAny: false));
  }

  [Fact]
  public void RequiresInteractiveConfirmation_false_when_environment_ack_set()
  {
    global::System.Environment.SetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable, "1");

    Assert.False(ListenAnySecurityPolicy.RequiresInteractiveConfirmation(configListenAny: true));
  }

  [Fact]
  public void RequiresInteractiveConfirmation_false_when_host_any_env_override_set()
  {
    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "1");

    Assert.False(ListenAnySecurityPolicy.RequiresInteractiveConfirmation(configListenAny: true));

    Assert.True(ArcanumEnvironment.IsHostAnyEnabled(false));
  }

  [Theory]
  [InlineData("maybe")]
  [InlineData("yes")]
  [InlineData("enabled")]
  [InlineData("2")]
  public void RequiresInteractiveConfirmation_UnrecognisedEnvValue_StillRequiresConfirmation(string value)
  {
    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", value);

    Assert.True(ListenAnySecurityPolicy.RequiresInteractiveConfirmation(configListenAny: true));
  }

  [Theory]
  [InlineData("maybe", false)]
  [InlineData("   ", false)]
  [InlineData("", false)]
  [InlineData("1", true)]
  [InlineData("true", true)]
  [InlineData("TRUE", true)]
  [InlineData("0", true)]
  [InlineData("false", true)]
  public void IsHostAnyEnvironmentOverrideSet_is_true_only_for_a_recognised_boolean(string value, bool expected)
  {
    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", value);

    Assert.Equal(expected, ListenAnySecurityPolicy.IsHostAnyEnvironmentOverrideSet());
  }

  [Fact]
  public void RequiresInteractiveConfirmation_false_when_host_any_env_override_disables_listen_any()
  {
    global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "0");

    Assert.False(ListenAnySecurityPolicy.RequiresInteractiveConfirmation(configListenAny: true));
  }

  [Fact]
  public void PersistAcknowledgement_writes_marker_file()
  {
    ListenAnySecurityPolicy.PersistAcknowledgement();

    Assert.StartsWith(
      Path.GetFullPath(_testHome),
      Path.GetFullPath(ArcanumPaths.GrimoireDirectory),
      StringComparison.Ordinal);

    Assert.True(ListenAnySecurityPolicy.IsListenAnyAcknowledged());
  }

  [Fact]
  public void SecurityBanner_describes_https_only_any_ip_not_plaintext()
  {
    Assert.Contains("HTTPS only", ListenAnySecurityPolicy.SecurityBanner, StringComparison.Ordinal);

    Assert.DoesNotContain("plaintext HTTP", ListenAnySecurityPolicy.SecurityBanner, StringComparison.Ordinal);

    Assert.Contains("HTTPS only", ListenAnySecurityPolicy.InteractiveConfirmPrompt, StringComparison.Ordinal);
  }
}
