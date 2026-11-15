using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>Closed classifications for a reported tool operation that did not succeed.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ToolOutcomeCode>))]
public enum ToolOutcomeCode
{
    [JsonStringEnumMemberName("workspacePolicyDenied")]
    WorkspacePolicyDenied = 1,

    [JsonStringEnumMemberName("notFound")]
    NotFound,

    [JsonStringEnumMemberName("notADirectory")]
    NotADirectory,

    [JsonStringEnumMemberName("invalidRange")]
    InvalidRange,

    [JsonStringEnumMemberName("invalidArguments")]
    InvalidArguments,

    [JsonStringEnumMemberName("accessDenied")]
    AccessDenied,

    [JsonStringEnumMemberName("notAFile")]
    NotAFile,

    [JsonStringEnumMemberName("hardLinkDenied")]
    HardLinkDenied,

    [JsonStringEnumMemberName("protectedPathDenied")]
    ProtectedPathDenied,

    [JsonStringEnumMemberName("externalToolError")]
    ExternalToolError,

    [JsonStringEnumMemberName("nativeOperationFailed")]
    NativeOperationFailed,

    [JsonStringEnumMemberName("nativePolicyDenied")]
    NativePolicyDenied,
}

/// <summary>Code-owned public diagnostic copy, separate from assistant answer text.</summary>
public sealed record ToolOutcome(ToolOutcomeCode Code)
{
    public bool Denied => Code is ToolOutcomeCode.WorkspacePolicyDenied
        or ToolOutcomeCode.AccessDenied
        or ToolOutcomeCode.HardLinkDenied
        or ToolOutcomeCode.ProtectedPathDenied
        or ToolOutcomeCode.NativePolicyDenied;

    public string Message => Code switch
    {
        ToolOutcomeCode.WorkspacePolicyDenied => "The workspace sandbox refused that path. Use a permitted path relative to the workspace root.",
        ToolOutcomeCode.NotFound => "The requested file or directory was not found in the workspace.",
        ToolOutcomeCode.NotADirectory => "The requested workspace path is not a directory.",
        ToolOutcomeCode.InvalidRange => "The requested line range is invalid or exceeds the supported limit. Use a smaller range with 1 <= startLine <= endLine.",
        ToolOutcomeCode.InvalidArguments => "The file tool arguments are invalid. Supply the required values in the advertised tool schema.",
        ToolOutcomeCode.AccessDenied => "Access to the requested workspace file or directory was denied.",
        ToolOutcomeCode.NotAFile => "The requested workspace path is not a regular file.",
        ToolOutcomeCode.HardLinkDenied => "This file has more than one hard link and cannot be read or written through the sandbox.",
        ToolOutcomeCode.ProtectedPathDenied => "That path is protected workspace metadata (.git or .arcanum) and cannot be written through the file tools.",
        ToolOutcomeCode.ExternalToolError => "The external tool reported an operation error. Its diagnostic details are untrusted and were not exposed.",
        ToolOutcomeCode.NativeOperationFailed => "The native tool reported an operation failure.",

        ToolOutcomeCode.NativePolicyDenied => "The native tool refused the operation under its policy.",

        _ => string.Empty,
    };

    [JsonIgnore]
    public bool IsKnownNativeFailure => Code is ToolOutcomeCode.WorkspacePolicyDenied
        or ToolOutcomeCode.NotFound
        or ToolOutcomeCode.NotADirectory
        or ToolOutcomeCode.InvalidRange
        or ToolOutcomeCode.InvalidArguments
        or ToolOutcomeCode.AccessDenied
        or ToolOutcomeCode.NotAFile
        or ToolOutcomeCode.HardLinkDenied
        or ToolOutcomeCode.ProtectedPathDenied;
}
