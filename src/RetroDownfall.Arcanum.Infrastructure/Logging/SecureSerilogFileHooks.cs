using System.Diagnostics.CodeAnalysis;
using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog.Sinks.File;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

[ExcludeFromCodeCoverage] // Reason: Serilog file sink hook wiring.
internal sealed class SecureSerilogFileHooks : FileLifecycleHooks
{

    public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
    {

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        return base.OnFileOpened(path, underlyingStream, encoding);

    }

}
