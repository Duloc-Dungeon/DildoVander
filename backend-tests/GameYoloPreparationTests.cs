using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Game;

internal static class GameYoloPreparationTests
{
    private static readonly GameYoloProfile Left = new("alice", "Alice", "Fikční knihovna", "Měsíční hůlka",
        ["Pečlivá při práci s hodinovými strojky."], WandAffinity: "Měsíční světlo", WandWeakness: "Hluk", SpellNames: ["Světelný bod"]);
    private static readonly GameYoloProfile Right = new("bob", "Bob", "Fikční nádraží", "Stínová hůlka",
        ["Snaží se dorazit dříve než vlastní zavazadlo."], WandAffinity: "Stíny", WandWeakness: "Vlhko", SpellNames: ["Stínová značka"]);

    internal static void Run()
    {
        var setting = Setting();
        OpenAiGameYoloProvider.ValidatePreparation(setting);
        var boundaryPlan = setting.ChallengerPlans[0] with
        {
            Title = new string('T', 100), Benefit = new string('B', 240), Cost = new string('C', 240)
        };
        var boundarySetting = setting with { ChallengerPlans = [boundaryPlan, .. setting.ChallengerPlans.Skip(1)] };
        OpenAiGameYoloProvider.ValidatePreparation(boundarySetting);
        List<GameYoloDecision> boundaryLeft = [GameYoloScenario.Select(1, "levitate", [], [], boundarySetting.ChallengerPlans)];
        List<GameYoloDecision> boundaryRight = [GameYoloScenario.Select(1, "levitate", [], [], setting.OpponentPlans)];
        GameYoloScenario.Reveal(boundaryLeft, boundaryRight, 1, boundarySetting);
        Check(boundaryLeft[0].Consequence.Length == 589 && boundaryLeft[0].Consequence.EndsWith(boundaryPlan.Cost, StringComparison.Ordinal),
            "maximum accepted card content is retained in the bounded first-act reveal");

        Check(setting.Id == "ai-scenario-v1" && setting.Title == "Cvrček před odjezdem"
            && setting.ChallengerPlans.Length == 3 && setting.OpponentPlans.Length == 3
            && setting.ChallengerFinale?.Length == 3 && setting.OpponentFinale?.Length == 3,
            "an invented scenario has six complete route cards and six concrete ending cards");
        var contaminated = Left with
        {
            Decisions = [new(1, "private-choice", "PRIVATE_DECISION_SENTINEL", "", "", "")],
            Setting = setting with { Title = "PRIVATE_SNAPSHOT_SENTINEL" }
        };
        using var request = JsonDocument.Parse(OpenAiGameYoloProvider.BuildPreparationRequest(contaminated, Right));
        var root = request.RootElement;
        Check(root.GetProperty("max_output_tokens").GetInt32() == 5000
            && !root.GetProperty("store").GetBoolean()
            && root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(),
            "scenario preparation is a bounded strict structured request");
        using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
        var left = input.RootElement.GetProperty("challenger");
        var right = input.RootElement.GetProperty("opponent");
        Check(left.GetProperty("name").GetString() == Left.Name
            && left.GetProperty("wandAffinity").GetString() == Left.WandAffinity
            && left.GetProperty("spellNames")[0].GetString() == Left.SpellNames![0]
            && right.GetProperty("wandWeakness").GetString() == Right.WandWeakness
            && right.GetProperty("traits")[0].GetString() == Right.Traits[0]
            && !left.TryGetProperty("decisions", out _) && !left.TryGetProperty("setting", out _),
            "both allowlisted characters and actual wand facts enter preparation without stored decisions or settings");
        Check(!root.GetProperty("input").GetString()!.Contains("PRIVATE_", StringComparison.Ordinal),
            "unrelated private snapshot fields cannot leak through profile serialization");
        var schema = root.GetProperty("text").GetProperty("format").GetProperty("schema");
        var plans = schema.GetProperty("properties").GetProperty("challengerPlans");
        Check(plans.GetProperty("minItems").GetInt32() == 3 && plans.GetProperty("maxItems").GetInt32() == 3
            && plans.GetProperty("items").GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).Contains("attack")
            && plans.GetProperty("items").GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).Contains("guard"),
            "the model must provide concrete counteractions with every initial plan");

        Reject(payload => payload["id"] = "tea-inspector-v1");
        Reject(payload => payload["title"] = new string('x', 81));
        Reject(payload => payload["situation"] = new string('x', 651));
        Reject(payload => payload["receipt"] = "  ");
        Reject(payload => payload["objective"] = "Two\nlines");
        Reject(payload => payload["unexpected"] = "private field");
        Reject(payload => payload.Remove("selectionReason"));
        Reject(payload => payload.Remove("opponentFinale"));
        Reject(payload => payload["opponentPlans"] = null);
        Reject(payload => payload["challengerPlans"]!.AsArray().RemoveAt(2));
        Reject(payload => payload["opponentFinale"]!.AsArray().Add(payload["opponentFinale"]![0]!.DeepClone()));
        Reject(payload => payload["challengerPlans"]![1]!["id"] = "levitate");
        Reject(payload => payload["opponentFinale"]![0]!["id"] = "unknown-ending");
        Reject(payload => payload["challengerPlans"]![0]!["guard"] = "");
        Reject(payload => payload["opponentPlans"]![1]!.AsObject().Remove("attack"));
        Reject(payload => payload["challengerFinale"]![1]!["benefit"] = new string('x', 241));
        Reject(payload => payload["opponentFinale"]![2]!["cost"] = "");
        Reject(payload => payload["opponentPlans"]![0]!["guard"] = new string('x', 161));
        ExpectInvalid(() => Parse("{bad json"));
        ExpectInvalid(() => Parse(Payload().ToJsonString(), "incomplete"));
        ExpectInvalid(() => Parse(new string('x', 65_537)));
        ExpectInvalid(() => OpenAiGameYoloProvider.BuildPreparationRequest(Left with { WandAffinity = new string('x', 40_000) }, Right));
        ExpectInvalid(() => OpenAiGameYoloProvider.ValidatePreparation(setting with { OpponentFinale = null }));
        ExpectInvalid(() => OpenAiGameYoloProvider.ValidatePreparation(setting with
        {
            ChallengerPlans = [setting.ChallengerPlans[0] with { Attack = null }, setting.ChallengerPlans[1], setting.ChallengerPlans[2]]
        }));
        var reordered = Payload();
        var cards = reordered["challengerPlans"]!.AsArray();
        var first = cards[0]!.DeepClone();
        cards.RemoveAt(0);
        cards.Add(first);
        Check(Parse(reordered.ToJsonString()).ChallengerPlans.Select(card => card.Id)
            .SequenceEqual(["levitate", "tray", "cup"]),
            "valid reordered cards normalize to stable internal route order");
        Console.WriteLine("YOLO preparation: allowlisted paired profiles, strict schema, complete generated cards, bounded parsing and malformed-output rejection passed offline.");
    }

    internal static GameYoloSetting Setting() => Parse(Payload().ToJsonString());

    private static JsonObject Payload()
    {
        JsonArray Cards(bool finale, string side)
        {
            string[] ids = finale ? ["finish", "adapt", "risk"] : ["levitate", "tray", "cup"];
            var result = new JsonArray();
            foreach (var id in ids)
            {
                var card = new JsonObject
                {
                    ["id"] = id, ["title"] = $"{side}: cesta {id}",
                    ["benefit"] = finale ? id switch
                    {
                        "finish" => "Původní cesta doručí cvrčka pouze s předchozí pojistkou.",
                        "adapt" => "Cvrčka odneseš v ruce vždy, i přes soupeřův zásah, ale pomalu.",
                        _ => "Připravená zkratka doručí cvrčka rychle, pokud ji soupeř nenarušil."
                    } : "Cvrček překoná cestu k odjezdové tabuli.",
                    ["cost"] = finale ? id switch
                    {
                        "finish" => "Bez pojistky z druhého dějství původní cesta selže.",
                        "adapt" => "Pomalé doručení přenechá výhodu úspěšné připravené zkratce.",
                        _ => "Bez přípravy nebo po soupeřově zásahu zkratka selže."
                    } : "Nezajištěné vodítko může soupeř posunout."
                };
                if (!finale)
                {
                    card["guard"] = "Připevnit vodítko ke stojanu tabule";
                    card["attack"] = "Posunout soupeřovo nezajištěné vodítko";
                }
                result.Add(card);
            }
            return result;
        }
        return new JsonObject
        {
            ["id"] = "ai-scenario-v1", ["title"] = "Cvrček před odjezdem",
            ["objective"] = "Doručit mechanického cvrčka pod odjezdovou tabuli bez poškození.",
            ["situation"] = "Odjezdová tabule odmítá ukázat čas bez cvrčka. Přesnost si představuje slyšitelně.",
            ["objectName"] = "mechanický cvrček", ["destination"] = "místo pod odjezdovou tabulí",
            ["receipt"] = "Mechanický cvrček je pod odjezdovou tabulí bez poškození.",
            ["selectionReason"] = "Alice využije pečlivost a světlo hůlky; Bob nádražní zkušenost a stínové značení.",
            ["challengerPlans"] = Cards(false, "Alice"), ["opponentPlans"] = Cards(false, "Bob"),
            ["challengerFinale"] = Cards(true, "Alice"), ["opponentFinale"] = Cards(true, "Bob")
        };
    }

    private static GameYoloSetting Parse(string payload, string status = "completed")
    {
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status, output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = payload } } } }
        }));
        return OpenAiGameYoloProvider.ParsePreparation(response.RootElement, Left, Right);
    }

    private static void Reject(Action<JsonObject> change)
    {
        var payload = Payload();
        change(payload);
        ExpectInvalid(() => Parse(payload.ToJsonString()));
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed YOLO preparation was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("YOLO preparation test failed: " + message); }
    }
}
