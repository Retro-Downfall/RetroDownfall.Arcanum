namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class MissingMasterApiKeyException : InvalidOperationException
{

    public const string MessageText =
        "No API key found. Run 'arcanum serve' once to generate and store a key.";

    public MissingMasterApiKeyException() : base(MessageText)
    {

    }

}
