using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private static JsonElement BuildCohortToolSchema(string name)
    {
        JsonElement creation = BuildTaskToolSchema("create_agent_task");

        using MemoryStream bytes = new();

        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();

            writer.WriteString("type", "object");

            writer.WritePropertyName("properties");

            writer.WriteStartObject();

            if (name == "create_cohort")
            {
                SchemaProperty(writer, "name", "{\"type\":\"string\",\"minLength\":1}");

                SchemaProperty(writer, "policy", "{\"type\":\"string\",\"enum\":[\"all\",\"first\",\"quorum\"]}");

                SchemaProperty(writer, "quorum", "{\"type\":\"integer\",\"minimum\":1}");

                SchemaProperty(writer, "resultType", "{\"type\":\"string\",\"enum\":[\"text/plain\",\"application/json\"]}");

                writer.WritePropertyName("grant");

                creation.GetProperty("properties").GetProperty("grant").WriteTo(writer);
            }
            else
            {
                SchemaProperty(writer, "cohortId", "{\"type\":\"string\",\"format\":\"uuid\"}");

                SchemaProperty(writer, "expectedRevision", "{\"type\":\"integer\",\"minimum\":1}");

                if (name == "remove_cohort_member") SchemaProperty(writer, "memberId", "{\"type\":\"string\",\"format\":\"uuid\"}");

                if (name == "admit_cohort_member")
                {
                    foreach (JsonProperty property in creation.GetProperty("properties").EnumerateObject()) property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();

            SchemaProperty(writer, "required", name switch
            {
                "create_cohort" => "[\"name\",\"policy\",\"resultType\"]",
                "admit_cohort_member" => "[\"cohortId\",\"expectedRevision\",\"goal\"]",
                "remove_cohort_member" => "[\"cohortId\",\"memberId\",\"expectedRevision\"]",
                _ => "[\"cohortId\",\"expectedRevision\"]",
            });

            writer.WriteBoolean("additionalProperties", false);

            if (name == "create_cohort")
            {
                SchemaProperty(writer, "oneOf", "[{\"properties\":{\"policy\":{\"const\":\"quorum\"}},\"required\":[\"quorum\"]},{\"properties\":{\"policy\":{\"enum\":[\"all\",\"first\"]}},\"not\":{\"required\":[\"quorum\"]}}]");
            }
            else if (name == "admit_cohort_member")
            {
                SchemaProperty(writer, "not", "{\"required\":[\"agent\",\"cardId\"]}");
            }

            writer.WriteEndObject();
        }

        using JsonDocument schema = JsonDocument.Parse(bytes.ToArray());

        return schema.RootElement.Clone();
    }

    private static void SchemaProperty(Utf8JsonWriter writer, string name, string json)
    {
        writer.WritePropertyName(name);

        using JsonDocument value = JsonDocument.Parse(json);

        value.RootElement.WriteTo(writer);
    }
}
