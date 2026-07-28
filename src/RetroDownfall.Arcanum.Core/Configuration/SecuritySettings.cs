namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SecuritySettings
{

    /// <summary>
    /// When <c>false</c> (default), <c>execute_command</c> / <c>run_spell_script</c> children require an OS
    /// filesystem jail where one is active for this beta (macOS deprecated <c>sandbox-exec</c>). Linux Landlock
    /// is present in-tree but inactive — fail-closed unless this escape hatch is true. If the jail cannot be
    /// applied, the tool is denied rather than running unbounded. When <c>true</c>, Arcanum logs a warning
    /// (platform, tool, campaign id) and runs with resource limits / env scrub only (no FS jail). Windows never
    /// provides an FS jail; when Sanctum path-boundary enforcement is active, those tools are denied regardless
    /// of this flag (escape hatch does not bypass that denial). This MVP is filesystem-only — it does not
    /// isolate network use by child binaries.
    /// </summary>
    public bool AllowUnsandboxedToolChildren { get; set; } = false;

    public bool MetricsRequireApiKey { get; set; } = true;

    public WardPolicySettings Ward { get; set; } = new();

    public GuardrailsPolicySettings Guardrails { get; set; } = new();

    public string[] PerceptionWorkspaceRoots { get; set; } = [];

    public string[] SpellWorkspaceRoots { get; set; } = [];

    public string[] CampaignRoots { get; set; } = [];

    public string[] AllowedUploadMimeTypes { get; set; } = [];

    public string[] AllowedImageMimeTypes { get; set; } =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/bmp",
    ];

}
