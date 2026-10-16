using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The attribute keys the Windows broker hands to <c>UpdateProcThreadAttribute</c>. A key is a number,
/// and a wrong one is not a compile error: the call fails with <c>ERROR_INVALID_PARAMETER</c> or sets a
/// different attribute from the one named, so the broker exits 73 before it has created any process.
/// The expected values are rebuilt here from the SDK's own recipe and the <c>PROC_THREAD_ATTRIBUTE_NUM</c>
/// enumeration, so the pin does not restate the constant it checks.
/// </summary>
public sealed class WindowsProcThreadAttributeTests
{
    // winbase.h: ProcThreadAttributeValue(Number, Thread, Input, Additive) =
    //   (Number & 0xFFFF) | (Thread ? 0x10000 : 0) | (Input ? 0x20000 : 0) | (Additive ? 0x40000 : 0)
    private const uint ThreadFlag = 0x00010000;

    private const uint InputFlag = 0x00020000;

    private const uint AdditiveFlag = 0x00040000;

    // The PROC_THREAD_ATTRIBUTE_NUM enumeration, in declaration order.
    private const ushort ProcThreadAttributeHandleList = 2;

    private const ushort ProcThreadAttributeIdealProcessor = 5;

    private const ushort ProcThreadAttributeSecurityCapabilities = 9;

    [Fact]
    public void The_security_capabilities_key_is_the_input_attribute_numbered_nine()
    {
        Assert.Equal(
            WindowsProcThreadAttribute.SecurityCapabilities,
            AttributeValue(ProcThreadAttributeSecurityCapabilities, thread: false, input: true, additive: false));
    }

    [Fact]
    public void The_handle_list_key_is_the_input_attribute_numbered_two()
    {
        Assert.Equal(
            WindowsProcThreadAttribute.HandleList,
            AttributeValue(ProcThreadAttributeHandleList, thread: false, input: true, additive: false));
    }

    /// <summary>
    /// The key the broker carried before: number five is the ideal-processor attribute, which is a
    /// per-thread input of a different size, so it never marked a process as an AppContainer.
    /// </summary>
    [Fact]
    public void The_security_capabilities_key_is_not_the_ideal_processor_key()
    {
        Assert.NotEqual(
            WindowsProcThreadAttribute.SecurityCapabilities,
            AttributeValue(ProcThreadAttributeIdealProcessor, thread: false, input: true, additive: false));

        Assert.NotEqual(
            WindowsProcThreadAttribute.SecurityCapabilities,
            AttributeValue(ProcThreadAttributeIdealProcessor, thread: true, input: true, additive: false));
    }

    [Theory]
    [InlineData((ushort)0, false, true, false, 0x00020000u)]
    [InlineData((ushort)2, false, true, false, 0x00020002u)]
    [InlineData((ushort)5, true, true, false, 0x00030005u)]
    [InlineData((ushort)9, false, true, false, 0x00020009u)]
    [InlineData((ushort)1, false, true, true, 0x00060001u)]
    public void The_recipe_reproduces_the_documented_sdk_values(
        ushort number,
        bool thread,
        bool input,
        bool additive,
        uint expected)
    {
        Assert.Equal(expected, AttributeValue(number, thread, input, additive));
    }

    private static uint AttributeValue(ushort number, bool thread, bool input, bool additive) =>
        (uint)number
        | (thread ? ThreadFlag : 0u)
        | (input ? InputFlag : 0u)
        | (additive ? AdditiveFlag : 0u);
}
