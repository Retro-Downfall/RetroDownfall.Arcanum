using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private Task<McpToolsCallResultWire> ExecuteUnboundTaskAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(ToolError($"{ErrorCodes.Guild.ToolBoundaryUnavailable}: A live Guild Quest invocation is required."));

    internal static string DescribeGuildTool(string name) => name switch
    {
        "create_agent_task" => "Admit an asynchronous child Guild Quest. Omit agent for local work; agent selects an exact configured remote URL. cardId selects a local Card and excludes agent. Returns the admitted Quest without waiting.",
        "send_agent_message" => "Send an informational message or continue input-required work in the existing Quest relationship. Continuation requires expectedRevision and cannot widen authority.",
        "get_agent_task" => "Read the exact current Quest projection in the authorized task subtree.",
        "cancel_agent_task" => "Request subtree cancellation; the receipt distinguishes the accepted request from actual convergence.",
        "list_agents" => "List Cards visible in the current Guild owner scope using a bounded continuation page.",
        "search_memory" => "Search authorized Session, attachment, workspace, Saga and Lexicon memory. Results identify their store and scoped availability. Long content is explicitly excerpted; continuation preserves remaining matches. save_memory and delete_memory modify only authorized Lexicon entries.",
        _ => throw new ArgumentException("Unknown Guild tool.", nameof(name)),
    };

    internal static JsonElement BuildTaskToolSchema(string name)
    {
        string schema = name switch
        {
            "create_agent_task" => """
                {
                  "type":"object",
                  "properties":{
                    "goal":{"type":"string","minLength":1,"maxLength":65536},
                    "name":{"type":"string"},
                    "cardId":{"type":"string","format":"uuid"},
                    "agent":{"type":"string","minLength":1,"description":"Exact configured remote discovery URL; omit for local work. Mutually exclusive with cardId."},
                    "grant":{
                      "type":"object","description":"Requested subset of the parent grant. Omitted axes inherit the permitted remaining envelope; scope widening is refused and budget hints are visibly clamped.",
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
                        "budget":{
                          "type":"object","properties":{
                            "usd":{
                              "type":"object","properties":{
                                "mode":{"type":"string","enum":["finite","unlimited"]},
                                "value":{"type":["number","null"],"minimum":0}
                              },"required":["mode"],"additionalProperties":false,
                              "oneOf":[
                                {"properties":{"mode":{"const":"finite"},"value":{"type":"number"}},"required":["value"]},
                                {"properties":{"mode":{"const":"unlimited"},"value":{"type":"null"}}}
                              ]
                            },
                            "tokens":{
                              "type":"object","properties":{
                                "mode":{"type":"string","enum":["finite","unlimited"]},
                                "value":{"type":["integer","null"],"minimum":0,"maximum":9223372036854775807}
                              },"required":["mode"],"additionalProperties":false,
                              "oneOf":[
                                {"properties":{"mode":{"const":"finite"},"value":{"type":"integer"}},"required":["value"]},
                                {"properties":{"mode":{"const":"unlimited"},"value":{"type":"null"}}}
                              ]
                            }
                          },"required":["usd","tokens"],"additionalProperties":false
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
                    "kind":{"type":"string","enum":["message","continuation"]},
                    "contentType":{"type":"string","enum":["text/plain","application/json"]},
                    "body":{"type":"string","maxLength":65536},
                    "idempotencyKey":{"type":"string","format":"uuid"},
                    "expectedRevision":{"type":"integer","minimum":1,"maximum":9223372036854775807}
                  },"required":["questId","kind","contentType","body"],"additionalProperties":false,
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
