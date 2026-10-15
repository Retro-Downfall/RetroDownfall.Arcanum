namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// The <c>UpdateProcThreadAttribute</c> keys the Windows broker uses. Kept apart from the launcher,
/// which only Windows can load, so every host pins the numbers.
/// </summary>
/// <remarks>
/// A key is <c>ProcThreadAttributeValue(Number, Thread, Input, Additive)</c> from <c>winbase.h</c>: the
/// <c>PROC_THREAD_ATTRIBUTE_NUM</c> in the low sixteen bits and the thread (<c>0x10000</c>), input
/// (<c>0x20000</c>) and additive (<c>0x40000</c>) flags above it. Both keys used here are
/// process-level inputs, so each is its number with <c>0x20000</c> added. A key that is not one of the
/// SDK's does not fail to compile: the call is rejected with <c>ERROR_INVALID_PARAMETER</c>, or names a
/// different attribute from the one intended.
/// </remarks>
internal static class WindowsProcThreadAttribute
{

    /// <summary>
    /// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>: attribute number 2, the handles the child inherits.
    /// </summary>
    internal const uint HandleList = 0x00020002;

    /// <summary>
    /// <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>: attribute number 9, a
    /// <c>SECURITY_CAPABILITIES</c> that makes the new process an AppContainer process. Number 5 is
    /// the thread-level ideal-processor attribute, not this one.
    /// </summary>
    internal const uint SecurityCapabilities = 0x00020009;

}
