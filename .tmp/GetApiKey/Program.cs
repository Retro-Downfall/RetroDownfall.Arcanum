using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;

string json = """
{"providers":[{"name":"test","type":"OpenAICompatible","endpoint":"https://api.example.com/v1","apiKey":"sk-x","models":["gpt-4o"]}],"defaultModel":"gpt-4o"}
""";

try
{
    ArcanumSettings? settings = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ArcanumSettings);

    Console.WriteLine($"Providers: {settings?.Providers?.Length ?? -1}");

    Console.WriteLine($"DefaultModel: {settings?.DefaultModel ?? "null"}");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
}
