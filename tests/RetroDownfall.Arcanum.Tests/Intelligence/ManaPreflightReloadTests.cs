using System.Reflection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ManaPreflightReloadTests
{

    [Fact]
    public void OnChange_PreservesCodeOwnedMessageTokenCacheCapacity()
    {
        ArcanumSettings initial = new();
        ArcanumSettings reloaded = new()
        {
            Features = new FeatureSettings { ArchiveSearch = false },
        };
        int expectedCapacity = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            ArcanumRuntimeDefaults.Grimoire.MaxMessagesPerConversationLoad);
        TriggerableOptionsMonitor<ArcanumSettings> monitor = new(initial);

        ManaPreflight preflight = new(monitor);

        Assert.Equal(expectedCapacity, GetMessageTokenCacheCapacity(preflight));

        monitor.TriggerChange(reloaded);

        Assert.Equal(expectedCapacity, GetMessageTokenCacheCapacity(preflight));

    }

    private static int GetMessageTokenCacheCapacity(ManaPreflight preflight)
    {

        FieldInfo? cacheField = typeof(ManaPreflight).GetField(
            "_messageTokenCache",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(cacheField);

        object cache = cacheField!.GetValue(preflight)!;

        FieldInfo? capacityField = cache.GetType().GetField(
            "_capacity",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(capacityField);

        return (int)capacityField!.GetValue(cache)!;

    }

    private sealed class TriggerableOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {

        private T _current;

        private Action<T, string?>? _onChange;

        public TriggerableOptionsMonitor(T initial)
        {

            _current = initial;

        }

        public T CurrentValue => _current;

        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {

            _onChange = listener;

            return new NoopDisposable();

        }

        public void TriggerChange(T newValue)
        {

            _current = newValue;

            _onChange?.Invoke(newValue, null);

        }

        private sealed class NoopDisposable : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

}
