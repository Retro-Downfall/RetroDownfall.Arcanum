using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class InferenceTokenCounter
{

    internal static bool ShouldSkipCompressionPreflight(IReadOnlyList<MeAiChatMessage> messages, int minMessages) =>
        messages.Count <= minMessages;

    internal static int CountTokens(
        IReadOnlyList<MeAiChatMessage> messages,
        Tokenizer tokenizer,
        int perMessageOverheadTokens)
    {

        int total = 0;

        foreach (MeAiChatMessage message in messages)
        {

            total += perMessageOverheadTokens;

            string? text = ExtractTextForCounting(message);

            if (string.IsNullOrEmpty(text))
            {

                continue;

            }

            total += tokenizer.CountTokens(text);

        }

        return total;

    }

    private static string? ExtractTextForCounting(MeAiChatMessage message)
    {

        string? direct = message.Text;

        if (!string.IsNullOrEmpty(direct))
        {

            return direct;

        }

        IList<AIContent> contents = message.Contents;

        if (contents.Count == 0)
        {

            return null;

        }

        var sb = new StringBuilder(256);

        foreach (AIContent item in contents)
        {

            if (item is TextContent tc)
            {

                if (!string.IsNullOrEmpty(tc.Text))
                {

                    _ = sb.Append(tc.Text);

                }

            }
            else
            {

                string? s = item.ToString();

                if (!string.IsNullOrEmpty(s))
                {

                    _ = sb.Append(s);

                }

            }

        }

        return sb.Length == 0 ? null : sb.ToString();

    }

}
