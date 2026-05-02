namespace RetroDownfall.Arcanum.Core.Primitives;

public readonly record struct Error(string Code, string Message)
{

    public static readonly Error None = new(string.Empty, string.Empty);

}
