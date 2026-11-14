namespace RetroDownfall.Arcanum.Tests.Collections;

/// <summary>
/// Gives whole-process resource measurements exclusive ownership of testhost work.
/// Serialization prevents concurrent fixture allocations; it does not clear retained memory.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResourceMeasurementCollection
{
    public const string Name = "ResourceMeasurement";
}
