using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class PublishedTestProcessIsolationTests
{
    [Fact]
    public void Rebased_process_temp_keeps_the_nested_home_eligible_for_test_credentials()
    {
        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create(
            "credential-boundary");

        try
        {
            isolation.Prepare();

            ProcessStartInfo start = new();

            isolation.ApplyTemporaryEnvironment(start);

            Assert.Equal(
                isolation.ProcessTemporaryDirectory,
                start.Environment["TMPDIR"]);

            Assert.Equal(
                isolation.ProcessTemporaryDirectory,
                start.Environment["TMP"]);

            Assert.Equal(
                isolation.ProcessTemporaryDirectory,
                start.Environment["TEMP"]);

            Assert.True(
                TestCredentialStorePolicy.IsEnabled(
                    "Testing",
                    isolation.TestHome,
                    "1",
                    isolation.ProcessTemporaryDirectory));

            string incorrectlyNestedTemporaryDirectory = Path.Combine(
                isolation.TestHome,
                "tmp");

            Directory.CreateDirectory(incorrectlyNestedTemporaryDirectory);

            Assert.False(
                TestCredentialStorePolicy.IsEnabled(
                    "Testing",
                    isolation.TestHome,
                    "1",
                    incorrectlyNestedTemporaryDirectory));
        }
        finally
        {
            isolation.Delete();
        }
    }
}
