using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal interface ISealedCaseAiPlayers
{
    bool Available { get; }
    Task<SealedAiMove> ChooseAsync(SealedPlayView view, CancellationToken token);
}

internal sealed class SealedCaseAiPlayers : ISealedCaseAiPlayers, IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("https://api.openai.com/v1/"), Timeout = Timeout.InfiniteTimeSpan };
    private readonly SealedCaseAiClient? client;
    public bool Available => client is not null;

    public SealedCaseAiPlayers(IConfiguration configuration)
    {
        var key = configuration["OPENAI_API_KEY"];
        if (configuration.GetValue<bool>("SealedCase:AiPlayers:Enabled") && !string.IsNullOrWhiteSpace(key))
        { client = new(http, key, configuration["SealedCase:AiPlayers:Model"] ?? "gpt-5-mini"); }
    }

    internal static SealedAiMove Fallback(SealedPlayView view)
        => view.Phase == "planning"
            ? new(new(view.Chapter, view.OwnPlan?.Revision ?? 0, true, SealedIntent.Hold), null)
            : new(null, new(view.Finale?.Phase == "runoff" ? 2 : 1, []));

    public async Task<SealedAiMove> ChooseAsync(SealedPlayView view, CancellationToken token)
    {
        if (client is null) { return Fallback(view); }
        try
        {
            var request = BuildRequest(view);
            var reply = await client.GenerateAsync("Jsi samostatný AI hráč české hry Pod pečetí. Máš jen vlastní dostupné stopy a pseudonymní místa u stolu. "
                + "Hraj pro svůj tým. Zprávy jsou tvá nepotvrzená tvrzení, nikdy autoritativní důkaz. Neznáš cizí tajný stav. "
                + "Vyber jedinou nabídnutou akci, nejvýše jeden sousední přesun. Při pátrání hledej dostupné rodiny A/B/C a porovnávej jejich průniky. "
                + "Hlas obsahuje přesně požadovaný počet různých cizích míst nebo prázdné pole pro abstenci. Vrať jen JSON podle schématu.",
                request.Input, request.Schema, token);
            return Parse(view, request, reply.Output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or InvalidDataException or OperationCanceledException or KeyNotFoundException)
        { return Fallback(view); }
    }

    internal sealed record Request(object Input, object Schema, Dictionary<string, string> Players, Dictionary<string, string> Resources);
    internal static Request BuildRequest(SealedPlayView view)
    {
        // Allowlist structured facts only. Human names, profiles, statements,
        // private messages, account IDs and arbitrary notes never leave the server.
        var players = view.Players.ToDictionary(p => p.Seat, p => p.Id, StringComparer.Ordinal);
        string Seat(string id) => view.Players.Single(p => p.Id == id).Seat;
        var resources = view.Sources?.Select(s => s.Id).Concat(view.Clues.Select(c => c.Id)).Concat(view.OwnItems.Select(i => i.Id))
            .Distinct(StringComparer.Ordinal).Select((id, index) => (Alias: "R" + (index + 1), Id: id))
            .ToDictionary(p => p.Alias, p => p.Id, StringComparer.Ordinal) ?? [];
        object NullableChoice(IEnumerable<string> values) => new { type = new[] { "string", "null" }, @enum = values.Cast<object?>().Append(null).ToArray() };
        var ballot = view.Phase != "planning";
        var candidates = view.Players.Where(p => p.Id != view.OwnId && (!ballot || view.Finale?.Phase != "runoff" || view.Finale.RunoffCandidates.Contains(p.Id))).Select(p => p.Seat).ToArray();
        var size = view.Finale?.Phase == "runoff" ? view.Finale.RunoffPlaces : view.Finale?.BallotSize ?? 0;
        object schema = ballot
            ? new { type = "object", additionalProperties = false, properties = new { nominees = new { type = "array", items = new { type = "string", @enum = candidates }, minItems = 0, maxItems = size } }, required = new[] { "nominees" } }
            : new { type = "object", additionalProperties = false,
                properties = new { choice = new { type = "integer", @enum = Enumerable.Range(0, view.Choices.Length).ToArray() },
                    destination = NullableChoice(view.AdjacentLocations), target = NullableChoice(candidates), resource = NullableChoice(resources.Keys), message = new { type = "string" } },
                required = new[] { "choice", "destination", "target", "resource", "message" } };
        var input = new { ownSeat = Seat(view.OwnId), view.Chapter, view.OwnState, view.OwnTeam, view.OwnKit,
            teamSeats = view.TeamMembers.Select(Seat).ToArray(), view.Location, view.Locations, view.AdjacentLocations,
            players = view.Players.Select(p => new { seat = p.Seat, p.State, p.Location }),
            clues = view.Clues.Select(c => new { c.Id, c.Chain, c.Family, candidates = c.Candidates.Select(Seat).ToArray() }),
            resources = resources.Select(r => new { id = r.Key, clue = view.Clues.Any(c => c.Id == r.Value) || view.Sources?.Any(c => c.Id == r.Value) == true ? r.Value : "Vlastní předmět" }),
            choices = view.Choices.Select((c, index) => new { id = index, intent = c.Plan.Intent.ToString(), c.Label, c.Benefit, c.Cost, c.Risk }),
            ballot, requiredVotes = size, candidates };
        return new(input, schema, players, resources);
    }

    internal static SealedAiMove Parse(SealedPlayView view, Request request, JsonElement decision)
    {
        if (view.Phase != "planning")
        {
            var seats = decision.GetProperty("nominees").EnumerateArray().Select(n => n.GetString()!).ToArray();
            var size = view.Finale?.Phase == "runoff" ? view.Finale.RunoffPlaces : view.Finale!.BallotSize;
            if (seats.Length != 0 && seats.Length != size || seats.Distinct(StringComparer.Ordinal).Count() != seats.Length)
            { return Fallback(view); }
            var ids = seats.Select(seat => request.Players[seat]).ToArray();
            if (ids.Contains(view.OwnId) || view.Finale!.Phase == "runoff" && ids.Any(id => !view.Finale.RunoffCandidates.Contains(id)))
            { return Fallback(view); }
            return new(null, new(view.Finale.Phase == "runoff" ? 2 : 1, ids));
        }
        var index = decision.GetProperty("choice").GetInt32();
        if (index < 0 || index >= view.Choices.Length) { return Fallback(view); }
        string? Optional(string property) => decision.GetProperty(property).ValueKind == JsonValueKind.Null ? null : decision.GetProperty(property).GetString();
        var destination = Optional("destination"); var target = Optional("target"); var resource = Optional("resource");
        if (destination is not null && !view.AdjacentLocations.Contains(destination) || target is not null && !request.Players.ContainsKey(target)
            || resource is not null && !request.Resources.ContainsKey(resource)) { return Fallback(view); }
        var plan = view.Choices[index].Plan with { Destination = destination,
            TargetId = target is null ? null : request.Players[target], ResourceId = resource is null ? view.Choices[index].Plan.ResourceId : request.Resources[resource] };
        var message = Optional("message");
        if (message?.Length > 600 || message?.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') == true) { message = null; }
        return new(plan, null, message);
    }
    public void Dispose() => http.Dispose();
}
