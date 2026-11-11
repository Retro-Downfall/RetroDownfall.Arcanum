using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private static JsonElement BuildMarketToolSchema(string name)
    {
        JsonElement grant = BuildTaskToolSchema("create_agent_task").GetProperty("properties").GetProperty("grant");

        using MemoryStream bytes = new();

        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();

            writer.WriteString("type", "object");

            writer.WriteStartObject("properties");

            string id = name switch { "post_work_offer" or "append_shared_note" or "read_shared_notes" => "cohortId", "withdraw_bid" => "bidId", _ => "offerId" };

            SchemaProperty(writer, id, "{\"type\":\"string\",\"format\":\"uuid\"}");

            if (name != "read_shared_notes") SchemaProperty(writer, name == "post_work_offer" ? "expectedCohortRevision" : "expectedRevision", "{\"type\":\"integer\",\"minimum\":1}");

            if (name == "post_work_offer")
            {
                SchemaProperty(writer, "skillId", "{\"type\":\"string\",\"minLength\":1}");

                SchemaProperty(writer, "goal", "{\"type\":\"string\",\"minLength\":1}");

                SchemaProperty(writer, "resultType", "{\"type\":\"string\",\"enum\":[\"text/plain\",\"application/json\"]}");

                writer.WritePropertyName("grant");

                grant.WriteTo(writer);
            }
            else if (name == "submit_bid")
            {
                SchemaProperty(writer, "note", "{\"type\":\"string\"}");

                SchemaProperty(writer, "claimedCostUsd", "{\"type\":\"number\",\"minimum\":0}");
            }
            else if (name == "award_work")
            {
                SchemaProperty(writer, "bidId", "{\"type\":\"string\",\"format\":\"uuid\"}");

                SchemaProperty(writer, "expectedCohortRevision", "{\"type\":\"integer\",\"minimum\":1}");
            }
            else if (name == "append_shared_note")
            {
                SchemaProperty(writer, "kind", "{\"type\":\"string\",\"enum\":[\"observation\",\"proposal\",\"correction\"]}");

                SchemaProperty(writer, "body", "{\"type\":\"string\"}");

                SchemaProperty(writer, "subjectId", "{\"type\":\"string\",\"format\":\"uuid\"}");

                SchemaProperty(writer, "correctsNoteId", "{\"type\":\"string\",\"format\":\"uuid\"}");
            }
            else if (name == "read_shared_notes")
            {
                SchemaProperty(writer, "cursor", "{\"type\":\"string\"}");

                SchemaProperty(writer, "pageSize", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}");
            }

            writer.WriteEndObject();

            SchemaProperty(writer, "required", name switch
            {
                "post_work_offer" => "[\"cohortId\",\"expectedCohortRevision\",\"skillId\",\"goal\",\"resultType\"]",
                "submit_bid" => "[\"offerId\",\"expectedRevision\",\"note\"]",
                "withdraw_bid" => "[\"bidId\",\"expectedRevision\"]",
                "award_work" => "[\"offerId\",\"expectedRevision\",\"expectedCohortRevision\",\"bidId\"]",
                "withdraw_work_offer" => "[\"offerId\",\"expectedRevision\"]",
                "append_shared_note" => "[\"cohortId\",\"expectedRevision\",\"kind\",\"body\"]",
                "read_shared_notes" => "[\"cohortId\"]",
                _ => throw new ArgumentException("Unknown distribution tool.", nameof(name)),
            });

            writer.WriteBoolean("additionalProperties", false);

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(bytes.ToArray());

        return document.RootElement.Clone();
    }
}
