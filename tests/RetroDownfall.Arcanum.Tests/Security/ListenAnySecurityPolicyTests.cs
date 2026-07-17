using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ListenAnySecurityPolicyTests : IDisposable
{

    private readonly string? _originalAck;

    private readonly string? _originalHostAny;

  public ListenAnySecurityPolicyTests()
  {

    _originalAck = global::System.Environment.GetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable);

    _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

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

  [Fact]
  public void PersistAcknowledgement_writes_marker_file()
  {

    ListenAnySecurityPolicy.PersistAcknowledgement();

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
