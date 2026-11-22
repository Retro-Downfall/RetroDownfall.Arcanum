using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private Task<McpToolsCallResultWire> ExecuteUnboundTaskAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(ToolError($"{ErrorCodes.Guild.ToolBoundaryUnavailable}: A live Guild Quest invocation is required.", denied: true));

    internal static string DescribeGuildTool(string name) => name switch
    {
        "read_file" => "Read a range of lines from a file in the granted workspace. Follow the continuation to read the rest of the range.",
        "list_directory" => "List entries within the Quest's granted workspace paths. Follow the continuation for additional entries.",
        "search_workspace" => "Find literal or regex matches in UTF-8 files within the Quest's granted workspace paths. Pass the returned nextCursor as cursor to continue.",
        "acquire_workspace_lease" => "Acquire a Quest-owned lease on granted files or subtrees. Retain its id, epoch and revision for renewal or release.",
        "renew_workspace_lease" => "Renew this Quest's held lease using its id, epoch and current revision.",
        "release_workspace_lease" => "Release this Quest's lease using its id, epoch and current revision. In-flight writes drain before reassignment.",
        "create_agent_task" => "Admit an asynchronous child Guild Quest. Omit agent for local work; agent selects an exact configured remote URL. cardId selects a local Card and excludes agent. Returns the admitted Quest without waiting.",
        "send_agent_message" => "Send an informational message to one questId or broadcast to the current cohortId membership; these addresses are mutually exclusive. Only questId can continue input-required work with expectedRevision. Messages never widen authority.",
        "get_agent_task" => "Read the exact current Quest projection in the authorized task subtree.",
        "cancel_agent_task" => "Request subtree cancellation; the receipt distinguishes the accepted request from actual convergence.",
        "list_agents" => "List Cards visible in the current Guild owner scope using a bounded continuation page.",
        "create_cohort" => "Create a result-collection Cohort under the current Quest with an immutable grant ceiling and all, first, or exact-byte quorum convergence. Membership remains open until explicitly closed.",
        "write_file" => "Write UTF-8 text to a file within the Quest's granted workspace paths.",
        "edit_file" => "Replace a literal UTF-8 text block inside the Quest's granted workspace paths.",
        "apply_patch" => "Apply a canonical unified diff entirely within the Quest's granted workspace paths.",
        "admit_cohort_member" => "Atomically issue one ordinary local or remote primary voting Quest into an open Cohort. Requires the current Cohort revision. Host identity and one-vote membership are derived from the accepted issue.",
        "close_cohort" => "Freeze an open Cohort's active voting membership and evaluate its retained results. The decision remains provisional until all associated work and accounting drain.",
        "remove_cohort_member" => "Withdraw an exact member and request its associated subtree cancellation. Removing a frozen member does not shrink the electorate or lower quorum.",
        "search_memory" => "Search authorized Session, attachment, workspace, Saga and Lexicon memory. Results identify their store and scoped availability. Long content is explicitly excerpted; continuation preserves remaining matches. save_memory and delete_memory modify only authorized Lexicon entries.",
        "post_work_offer" => "Publish work to eligible current Cohort members.",
        "submit_bid" => "Submit a bid as the current member.",
        "withdraw_bid" => "Withdraw your submitted bid.",
        "award_work" => "Award an open offer to a submitted bid.",
        "withdraw_work_offer" => "Withdraw your open work offer.",
        "append_shared_note" => "Append a shared Cohort note.",
        "read_shared_notes" => "Read a bounded page of shared Cohort notes.",
        _ => throw new ArgumentException("Unknown Guild tool.", nameof(name)),
    };

    internal static JsonElement BuildTaskToolSchema(string name, WorkspaceSearchSettings? searchSettings = null)
    {
        if (name == "write_file") return BuildWriteFileSchema();

        if (name == "edit_file") return BuildReplaceTextBlockSchema();

        if (name == "apply_patch") return BuildApplyPatchSchema(ArcanumRuntimeDefaults.CodingTools.Patch);

        if (name == "read_file") return BuildReadFileChunkSchema(includeContinuation: true);

        if (name == "list_directory") return BuildListDirectorySchema();

        if (name == "search_workspace") return BuildSearchWorkspaceSchema(searchSettings ?? ArcanumRuntimeDefaults.CodingTools.Search);

        if (GuildLocalToolRegistry.IsMarketTool(name)) return BuildMarketToolSchema(name);

        if (GuildLocalToolRegistry.IsCohortTool(name)) return BuildCohortToolSchema(name);

        string schema = name switch
        {
            "acquire_workspace_lease" => """
                {"type":"object","properties":{
                  "scopes":{"type":"array","minItems":1,"items":{"type":"object","properties":{
                    "relativePath":{"type":"string"},"kind":{"type":"string","enum":["file","subtree"]}},
                    "required":["relativePath","kind"],"additionalProperties":false}},
                  "idempotencyKey":{"type":"string","format":"uuid"}
                },"required":["scopes"],"additionalProperties":false}
                """,
            "renew_workspace_lease" or "release_workspace_lease" => """
                {"type":"object","properties":{
                  "leaseId":{"type":"string","format":"uuid"},"epoch":{"type":"integer","minimum":1},
                  "expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","format":"uuid"}
                },"required":["leaseId","epoch","expectedRevision"],"additionalProperties":false}
                """,
            "create_agent_task" => """
                {
                  "type":"object",
                  "properties":{
                    "goal":{"type":"string","minLength":1,"maxLength":65536},
                    "name":{"type":"string"},
                    "cardId":{"type":"string","format":"uuid"},
                    "agent":{"type":"string","minLength":1,"description":"Exact configured remote discovery URL; omit for local work. Mutually exclusive with cardId."},
                    "grant":{
                      "type":"object","description":"Requested subset of the parent grant. Omitted axes inherit the corresponding permitted authority.",
                      "properties":{
                        "workspaces":{
                          "type":"array","items":{
                            "type":"object","properties":{
                              "workspaceId":{"type":"string","format":"uuid"},
                              "relativeRoot":{"type":"string"},
                              "access":{"type":"string","enum":["read","readWrite"]}
                            },"required":["workspaceId","relativeRoot","access"],"additionalProperties":false
                          }
                        },
                        "tools":{
                          "type":"array","items":{
                            "type":"object","properties":{
                              "sourceKind":{"type":"string","enum":["builtIn","internalMcp","adapter"]},
                              "sourceRegistrationId":{"type":"string"},
                              "name":{"type":"string"}
                            },"required":["sourceKind","sourceRegistrationId","name"],"additionalProperties":false
                          }
                        },
                        "memory":{
                          "type":"array","items":{
                            "type":"object","properties":{
                              "store":{"type":"string","enum":["transcript","saga","lexicon","attachment","tapestry"]},
                              "operation":{"type":"string","enum":["read","append","delete"]},
                              "scope":{
                                "type":"object","properties":{
                                  "kind":{"type":"string","enum":["global","campaign","session"]},
                                  "id":{"type":["string","null"],"format":"uuid"}
                                },"required":["kind"],"additionalProperties":false,
                                "oneOf":[
                                  {"properties":{"kind":{"const":"global"},"id":{"type":"null"}}},
                                  {"properties":{"kind":{"enum":["campaign","session"]},"id":{"type":"string"}},"required":["id"]}
                                ]
                              }
                            },"required":["store","operation","scope"],"additionalProperties":false
                          }
                        },
                        "delegation":{"type":"array","items":{"type":"string","enum":["local","remote","cohort"]}},
                        "expiresAtUtc":{"type":["string","null"],"format":"date-time"}
                      },"additionalProperties":false
                    }
                  },
                  "required":["goal"],"not":{"required":["agent","cardId"]},"additionalProperties":false
                }
                """,
            "send_agent_message" => """
                {
                  "type":"object","properties":{
                    "questId":{"type":"string","format":"uuid"},
                    "cohortId":{"type":"string","format":"uuid"},
                    "kind":{"type":"string","enum":["message","continuation"]},
                    "contentType":{"type":"string","enum":["text/plain","application/json"]},
                    "body":{"type":"string","maxLength":65536},
                    "idempotencyKey":{"type":"string","format":"uuid"},
                    "expectedRevision":{"type":"integer","minimum":1,"maximum":9223372036854775807}
                  },"required":["kind","contentType","body"],"additionalProperties":false,
                  "allOf":[{"oneOf":[
                    {"required":["questId"],"not":{"required":["cohortId"]}},
                    {"required":["cohortId"],"not":{"required":["questId"]},"properties":{"kind":{"const":"message"}}}
                  ]}],
                  "oneOf":[
                    {"properties":{"kind":{"const":"message"}},"not":{"required":["expectedRevision"]}},
                    {"properties":{"kind":{"const":"continuation"}},"required":["expectedRevision"]}
                  ]
                }
                """,
            "get_agent_task" => """
                {"type":"object","properties":{"questId":{"type":"string","format":"uuid"}},"required":["questId"],"additionalProperties":false}
                """,
            "cancel_agent_task" => """
                {"type":"object","properties":{"questId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","format":"uuid"}},"required":["questId","expectedRevision"],"additionalProperties":false}
                """,
            "list_agents" => """
                {"type":"object","properties":{"source":{"type":"string","enum":["declaredGlobal","declaredWorkspace","derived","remote"]},"skill":{"type":"string"},"cursor":{"type":"string"},"pageSize":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
                """,
            _ => throw new ArgumentException("Unknown task tool.", nameof(name)),
        };

        using JsonDocument document = JsonDocument.Parse(schema);

        return document.RootElement.Clone();
    }
}
