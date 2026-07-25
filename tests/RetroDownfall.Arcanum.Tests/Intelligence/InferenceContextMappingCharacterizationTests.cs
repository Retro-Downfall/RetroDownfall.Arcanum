using System.Text.Json;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceContextMappingCharacterizationTests
{
    [Fact]
    public void MapToAiChatMessages_AssistantToolCalls_PreservesTextAndParsesArguments()
    {
        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(
        [
            new CoreChatMessage(
                "assistant",
                "calling",
                ToolCalls:
                [
                    new CoreToolCall("object", "lookup", """{"city":"Paris","count":2}"""),
                    new CoreToolCall("scalar", "lookup", "42"),
                    new CoreToolCall("invalid", "lookup", "{not-json"),
                    new CoreToolCall("empty", "lookup", " "),
                ]),
        ]);

        MeAiChatMessage message = Assert.Single(mapped);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(5, message.Contents.Count);
        Assert.Equal("calling", Assert.IsType<TextContent>(message.Contents[0]).Text);

        FunctionCallContent objectCall = Assert.IsType<FunctionCallContent>(message.Contents[1]);
        Assert.Equal("object", objectCall.CallId);
        Assert.Equal("lookup", objectCall.Name);
        Assert.NotNull(objectCall.Arguments);
        Assert.Equal(
            "Paris",
            Assert.IsType<JsonElement>(objectCall.Arguments["city"]).GetString());
        Assert.Equal(
            2,
            Assert.IsType<JsonElement>(objectCall.Arguments["count"]).GetInt32());

        FunctionCallContent scalarCall = Assert.IsType<FunctionCallContent>(message.Contents[2]);
        Assert.Equal(
            42,
            Assert.IsType<JsonElement>(scalarCall.Arguments!["value"]).GetInt32());

        FunctionCallContent invalidCall = Assert.IsType<FunctionCallContent>(message.Contents[3]);
        Assert.Equal("{not-json", invalidCall.Arguments!["raw"]);

        FunctionCallContent emptyCall = Assert.IsType<FunctionCallContent>(message.Contents[4]);
        Assert.Null(emptyCall.Arguments);
    }

    [Fact]
    public void MapToAiChatMessages_AssistantToolCallsWithoutText_ContainsOnlyCalls()
    {
        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(
        [
            new CoreChatMessage(
                "assistant",
                string.Empty,
                ToolCalls: [new CoreToolCall("call-1", "clock", "{}")]),
        ]);

        MeAiChatMessage message = Assert.Single(mapped);
        FunctionCallContent call = Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents));
        Assert.Equal("call-1", call.CallId);
        Assert.Empty(call.Arguments!);
    }

    [Fact]
    public void MapToAiChatMessages_ContentParts_MapsValidImagesAndDropsMalformedParts()
    {
        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(
        [
            new CoreChatMessage(
                "user",
                "fallback",
                Name: "vision-user",
                ContentParts:
                [
                    new CoreContentPart("image_url", null, "data:image/png;base64,AQID", null),
                    new CoreContentPart("image_url", null, "data:;base64,BAU=", null),
                    new CoreContentPart("image_url", null, "https://example.test/image.png", null),
                    new CoreContentPart("image_url", null, "data:image/png;base64", null),
                    new CoreContentPart("image_url", null, "data:image/png,AAAA", null),
                    new CoreContentPart("image_url", null, "data:image/png;base64,***", null),
                    new CoreContentPart("image_url", null, "not a uri", null),
                    new CoreContentPart("image_url", null, " ", null),
                    new CoreContentPart("text", "caption", null, null),
                    new CoreContentPart("text", null, null, null),
                ]),
        ]);

        MeAiChatMessage message = Assert.Single(mapped);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("vision-user", message.AuthorName);
        Assert.Equal(4, message.Contents.Count);

        DataContent png = Assert.IsType<DataContent>(message.Contents[0]);
        Assert.Equal("image/png", png.MediaType);
        Assert.True(png.Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }));

        DataContent wildcard = Assert.IsType<DataContent>(message.Contents[1]);
        Assert.Equal("image/*", wildcard.MediaType);
        Assert.True(wildcard.Data.Span.SequenceEqual(new byte[] { 4, 5 }));

        UriContent remote = Assert.IsType<UriContent>(message.Contents[2]);
        Assert.Equal(new Uri("https://example.test/image.png"), remote.Uri);
        Assert.Equal("image/*", remote.MediaType);

        Assert.Equal("caption", Assert.IsType<TextContent>(message.Contents[3]).Text);
    }

    [Fact]
    public void MapToAiChatMessages_EmptyContentParts_ProducesNamedEmptyTextContent()
    {
        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(
        [
            new CoreChatMessage(
                "developer",
                "ignored fallback",
                Name: "policy",
                ContentParts:
                [
                    new CoreContentPart("text", null, null, null),
                    new CoreContentPart("image_url", null, "relative/image.png", null),
                ]),
        ]);

        MeAiChatMessage message = Assert.Single(mapped);
        Assert.Equal(ChatRole.System, message.Role);
        Assert.Equal("policy", message.AuthorName);
        Assert.Equal(string.Empty, Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
    }

    [Fact]
    public void MapToAiChatMessages_NullTextAndToolResult_UseEmptyContent()
    {
        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(
        [
            new CoreChatMessage("custom", null!, Name: "named-user"),
            new CoreChatMessage("tool", null!, ToolCallId: "call-1"),
        ]);

        Assert.Equal(ChatRole.User, mapped[0].Role);
        Assert.Equal(string.Empty, mapped[0].Text);
        Assert.Equal("named-user", mapped[0].AuthorName);

        Assert.Equal(ChatRole.Tool, mapped[1].Role);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(
            Assert.Single(mapped[1].Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal(string.Empty, result.Result);
    }

    [Fact]
    public void AppendContentsToLastMessage_MergesContentsAndPreservesMetadata()
    {
        List<MeAiChatMessage> messages =
        [
            new MeAiChatMessage(ChatRole.User, "prompt") { AuthorName = "author" },
        ];
        DataContent image = new(new byte[] { 1, 2 }, "image/png");

        InferenceContextBuilder.AppendContentsToLastMessage(messages, [image]);

        MeAiChatMessage merged = Assert.Single(messages);
        Assert.Equal(ChatRole.User, merged.Role);
        Assert.Equal("author", merged.AuthorName);
        Assert.Collection(
            merged.Contents,
            content => Assert.Equal("prompt", Assert.IsType<TextContent>(content).Text),
            content => Assert.Same(image, content));
    }

    [Fact]
    public void AppendContentsToLastMessage_NullEmptyOrMissingMessage_IsNoOp()
    {
        List<MeAiChatMessage> populated = [new(ChatRole.User, "prompt")];
        MeAiChatMessage original = populated[0];

        InferenceContextBuilder.AppendContentsToLastMessage(populated, null);
        InferenceContextBuilder.AppendContentsToLastMessage(populated, []);
        InferenceContextBuilder.AppendContentsToLastMessage([], [new TextContent("ignored")]);

        Assert.Same(original, Assert.Single(populated));
    }

    [Fact]
    public void PrependDynamicSystemMessage_Whitespace_IsNoOp()
    {
        List<MeAiChatMessage> messages = [new(ChatRole.User, "prompt")];

        InferenceContextBuilder.PrependDynamicSystemMessage(messages, " \t ");

        Assert.Equal(ChatRole.User, Assert.Single(messages).Role);
    }

    [Fact]
    public void BuildInitialMeAiChatMessages_MalformedScryingFocus_SkipsOnlyInvalidImage()
    {
        PingRequest request = new(
            Prompt: "ignored",
            StatelessMessages: [new CoreChatMessage("user", "prompt")],
            ScryingFoci:
            [
                new ScryingFocusDto("***", "image/png"),
                new ScryingFocusDto(Convert.ToBase64String([7, 8]), "image/png"),
            ]);

        List<MeAiChatMessage> messages =
            InferenceContextBuilder.BuildInitialMeAiChatMessages(request, null, "ignored");

        MeAiChatMessage message = Assert.Single(messages);
        Assert.Collection(
            message.Contents,
            content => Assert.Equal("prompt", Assert.IsType<TextContent>(content).Text),
            content =>
            {
                DataContent data = Assert.IsType<DataContent>(content);
                Assert.True(data.Data.Span.SequenceEqual(new byte[] { 7, 8 }));
            });
    }
}
