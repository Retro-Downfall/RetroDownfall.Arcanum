using Microsoft.Extensions.Options;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class TestOptionsSnapshot<T>(T current) : IOptionsSnapshot<T> where T : class
{

    public T Value => current;

    public T Get(string? name) => current;

}
