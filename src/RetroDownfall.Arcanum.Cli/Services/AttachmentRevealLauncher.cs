using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Cli.Services;

internal interface IAttachmentRevealLauncher
{

    string TryReveal(string absolutePath, out bool started);

}

internal sealed class AttachmentRevealLauncher : IAttachmentRevealLauncher
{

    public string TryReveal(string absolutePath, out bool started) =>
        SessionAttachmentReveal.TryReveal(
            absolutePath,
            isInteractive: true,
            out started);

}
