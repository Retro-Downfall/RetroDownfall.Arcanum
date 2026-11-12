using System.CommandLine;
using System.Globalization;
using RetroDownfall.Arcanum.Cli.Commands.Guild;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildGuild(IServiceProvider services)
    {
        DeferredHandler<GuildCommands> handler = new(services);

        Command guild = new("guild", "Inspect and manage sealed Guild work through the authenticated host.");

        Add(guild, "status", "Inspect feature, schema and capability state.", [], []);

        Command agent = Family("agent", "Discover visible pinned Agent Cards.");

        Add(agent, "list", "List one page of visible Agent Cards.", [], ["source", "skill", "cursor", "page-size"]);

        Add(agent, "show", "Show one exact Agent Card.", ["agent"], ["card-revision"]);

        Add(agent, "refresh", "Refresh a selected Agent or one source batch.", ["agent"], ["cursor", "idempotency-key"]);

        Command task = Family("task", "Issue and inspect Quests and exact subtrees.");

        Add(task, "create", "Issue a root Quest with an explicit grant.", [], ["goal", "goal-file", "grant-file!", "agent", "name", "session", "campaign", "workspace", "predecessor", "idempotency-key"]);

        Add(task, "delegate", "Issue an attenuated child under a live parent.", ["parent"], ["goal", "goal-file", "expected-revision!", "agent", "name", "grant-file", "idempotency-key"]);

        Add(task, "list", "List one bounded page of Quests.", [], ["root", "parent", "state", "cursor", "page-size"]);

        Add(task, "show", "Show Quest metadata and explicitly requested content.", ["task"], ["show-content"]);

        Add(task, "tree", "Read one page of an exact subtree.", ["task"], ["cursor", "page-size"]);

        Add(task, "message", "Send Operator-authored information or continuation.", ["task"], ["text", "text-file", "expected-revision!", "continue", "idempotency-key"]);

        Add(task, "cancel", "Request durable subtree cancellation.", ["task"], ["expected-revision!", "idempotency-key"]);

        Add(task, "spend", "Inspect exact subtree spend and remaining authority.", ["task"], []);

        Command signet = Family("signet", "Revoke stored authority without exposing credentials.");

        Add(signet, "revoke", "Revoke a stored Signet and request cancellation.", ["signet"], ["expected-revision!", "idempotency-key"]);

        Command content = Family("content", "Explicitly disclose authorized content revisions.");

        Add(content, "show", "Read an exact authorized content revision.", ["content-id"], ["revision!"]);

        Command watch = Family("watch", "Follow a projection or obtain one finite snapshot.");

        Add(watch, "tree", "Watch an exact Quest subtree.", ["task"], ["after", "ndjson"]);

        Add(watch, "cohort", "Watch a Cohort and its admitted work.", ["cohort"], ["after", "ndjson"]);

        Command cohort = Family("cohort", "Manage Cohort membership and deterministic convergence.");

        Add(cohort, "create", "Create a Cohort under a live issuer.", [], ["issuer!", "name!", "policy!", "result-type!", "quorum", "grant-file", "idempotency-key"]);

        Add(cohort, "list", "List one page of visible Cohorts.", [], ["root", "state", "cursor", "page-size"]);

        Add(cohort, "show", "Show Cohort metadata.", ["cohort"], []);

        Add(cohort, "admit", "Admit a child Quest to a Cohort.", ["cohort"], ["goal", "goal-file", "expected-revision!", "agent", "name", "grant-file", "idempotency-key"]);

        Add(cohort, "members", "List one page of Cohort members.", ["cohort"], ["cursor", "page-size"]);

        Add(cohort, "remove", "Withdraw a member and drain its subtree.", ["cohort", "member"], ["expected-revision!", "idempotency-key"]);

        Add(cohort, "close", "Freeze Cohort membership.", ["cohort"], ["expected-revision!", "idempotency-key"]);

        Add(cohort, "cancel", "Request Cohort cancellation and drain.", ["cohort"], ["expected-revision!", "idempotency-key"]);

        Add(cohort, "contributions", "List one page of result references.", ["cohort"], ["selected", "cursor", "page-size"]);

        Add(cohort, "convergence", "Inspect the stored convergence decision.", ["cohort"], []);

        Add(cohort, "member-work", "List primary and award Quest associations.", ["cohort", "member"], ["cursor", "page-size"]);

        Command offer = Family("offer", "Inspect and award skill-routed work.");

        Add(offer, "post", "Post an offer under a Cohort issuer.", ["cohort"], ["skill!", "goal", "goal-file", "result-type!", "expected-cohort-revision!", "grant-file", "idempotency-key"]);

        Add(offer, "list", "List one page of Cohort offers.", ["cohort"], ["state", "cursor", "page-size"]);

        Add(offer, "show", "Show a visible offer.", ["offer"], []);

        Add(offer, "withdraw", "Withdraw a work offer.", ["offer"], ["expected-revision!", "idempotency-key"]);

        Add(offer, "award", "Award an offer to an existing member bid.", ["offer"], ["bid!", "expected-revision!", "expected-cohort-revision!", "idempotency-key"]);

        Command bid = Family("bid", "Inspect member-authored bids.");

        Add(bid, "list", "List one page of bids for an offer.", ["offer"], ["cursor", "page-size"]);

        Command note = Family("note", "Append and inspect the shared ledger.");

        Add(note, "append", "Append an Operator-authored observation or correction.", ["cohort"], ["kind!", "text", "text-file", "subject", "corrects", "expected-revision!", "idempotency-key"]);

        Add(note, "list", "List one page of authorized notes.", ["cohort"], ["cursor", "page-size"]);

        Command lease = Family("lease", "Manage host-derived Operator workspace leases.");

        Add(lease, "acquire", "Acquire a complete path-scope set atomically.", [], ["workspace!", "scope!", "idempotency-key"]);

        Add(lease, "list", "List one page of authorized workspace leases.", [], ["workspace", "quest", "state", "cursor", "page-size"]);

        Add(lease, "renew", "Renew an exact Operator lease epoch and revision.", ["lease"], ["epoch!", "expected-revision!", "idempotency-key"]);

        Add(lease, "release", "Release or drain an exact Operator lease.", ["lease"], ["epoch!", "expected-revision!", "idempotency-key"]);

        return guild;

        Command Family(string name, string description)
        {
            Command family = new(name, description);

            guild.Add(family);

            return family;
        }

        void Add(Command parent, string name, string description, string[] arguments, string[] optionNames)
        {
            Command command = new(name, description);

            List<Argument<string?>> positionals = [];

            foreach (string argumentName in arguments)
            {
                Argument<string?> argument = new(argumentName)
                {
                    Arity = ArgumentArity.ZeroOrOne,

                    Description = $"Visible {argumentName} UUID, exact name, or unique UUID prefix; omit for an interactive picker.",
                };

                command.Add(argument);

                positionals.Add(argument);
            }

            Dictionary<string, Option> options = new(StringComparer.Ordinal);

            foreach (string declaredName in optionNames)
            {
                bool required = declaredName.EndsWith('!');

                string key = declaredName.TrimEnd('!');

                string optionName = "--" + key;

                Option option;

                if (key is "ndjson" or "show-content" or "continue" or "selected")
                {
                    option = new Option<bool>(optionName) { Description = Description(key) };
                }
                else if (key is "expected-revision" or "expected-cohort-revision" or "revision" or "epoch" or "card-revision" or "quorum" or "page-size")
                {
                    option = new Option<long?>(optionName) { Required = required, Description = Description(key) };
                }
                else if (key == "scope")
                {
                    option = new Option<string[]>(optionName) { Required = required, AllowMultipleArgumentsPerToken = true, Description = Description(key) };
                }
                else
                {
                    Option<string?> text = new(optionName) { Required = required, Description = Description(key) };

                    if (key == "policy") text.AcceptOnlyFromAmong("all", "first", "quorum");

                    if (key == "result-type") text.AcceptOnlyFromAmong("text/plain", "application/json");

                    if (key == "kind") text.AcceptOnlyFromAmong("observation", "proposal", "correction");

                    if (key == "source") text.AcceptOnlyFromAmong("declaredGlobal", "declaredWorkspace", "derived", "remote");

                    option = text;
                }

                options.Add(key, option);

                command.Add(option);
            }

            string path = parent == guild ? name : parent.Name + " " + name;

            command.SetAction(async (ParseResult parsed, CancellationToken cancellationToken) =>
            {
                Dictionary<string, object?> values = new(StringComparer.Ordinal);

                foreach ((string key, Option symbol) in options)
                {
                    values[key] = symbol switch
                    {
                        Option<bool> flag => parsed.GetValue(flag),

                        Option<long?> number => parsed.GetValue(number),

                        Option<string[]> many => parsed.GetValue(many),

                        Option<string?> text => parsed.GetValue(text),

                        _ => null,
                    };
                }

                return await handler.Value.ExecuteAsync(path,
                    new GuildCommandInput([.. positionals.Select(argument => parsed.GetValue(argument))], values), cancellationToken).ConfigureAwait(false);
            });

            parent.Add(command);
        }
    }

    private static string Description(string name) => name switch
    {
        "goal" or "text" => "Authored task data; mutually exclusive with its UTF-8 file option.",
        "goal-file" or "text-file" => "Bounded UTF-8 input file, or - to read stdin; mutually exclusive with inline text.",
        "grant-file" => "Bounded JSON grant file; complete GuildGrant at a root, GuildRequestedGrant when narrowing.",
        "idempotency-key" => "Mutation UUID; generated once when omitted and reused by transport retries.",
        "expected-revision" => "Required current resource revision; conflicts are reported without retrying the changed action.",
        "expected-cohort-revision" => "Required current Cohort aggregate revision.",
        "page-size" => "Requested records per page, 1 through 100; the byte limit may return fewer.",
        "cursor" => "Opaque cursor for the next bounded page or refresh batch.",
        "after" => "Opaque stream resume cursor; incompatible with finite --json snapshots.",
        "ndjson" => "Live machine output: one event/id/data JSON record per SSE frame; incompatible with --json.",
        "show-content" => "Explicitly fetch the authorized exact goal/result content revisions.",
        "scope" => "Repeatable relative-path:file or relative-path:subtree scope, acquired all together.",
        "epoch" => "Exact current lease epoch.",
        "revision" or "card-revision" => "Exact retained content or Card revision to inspect.",
        "continue" => "Answer the current input-required question instead of sending informational text.",
        "selected" => "Only contributions selected by the stored convergence decision.",
        "quorum" => "Positive quorum size; required only with --policy quorum.",
        _ => $"{name.Replace('-', ' ')} selector or value, verified by the authenticated host.",
    };
}
