namespace Kufr.Backend.Features.Game;

internal sealed record GameYoloOption(string Id, string Title, string Benefit, string Cost, string? Guard = null, string? Attack = null);
internal sealed record GameYoloSetup(GameYoloProfile Challenger, GameYoloProfile Opponent, GameYoloSetting? Setting = null);
internal sealed record GameYoloDecision(int Act, string Choice, string Title, string Benefit, string Cost, string Consequence, string? Guard = null, string? Attack = null);
internal sealed record GameYoloAct(int Act, GameYoloDecision Player, GameYoloDecision Opponent);
internal sealed record GameYoloScenarioView(string Id, string Title, string Objective, string Situation,
    int Act, bool OwnChoiceLocked, bool OpponentChoiceLocked, IReadOnlyList<GameYoloOption> Options,
    IReadOnlyList<GameYoloAct> Revealed, string? SelectionReason = null);
internal sealed record GameYoloDelivery(bool GoalMet, string Consequence);

// Authored rules, prices and consequences are resolved before the final AI verdict.
// The model describes and judges this record; it cannot invent a missing preparation.
internal static class GameYoloScenario
{
    internal const string Id = "tea-inspector-v1";
    internal const string Title = "Čaj pro kontrolora";
    internal const string Objective = "Dostat čaj na stůl v nepoškozeném šálku.";
    internal const string Situation = "Kontrolor čeká na čaj. Mezi konvicí a stolem je nerovná podlaha a otevřené okno. Kontrolor uznává pouze čaj v šálku; louže se dosud neprokázala uchem.";

    internal static GameYoloOption[] Options(int act, IReadOnlyList<GameYoloDecision> own,
        IReadOnlyList<GameYoloDecision> opponent, GameYoloOption[]? plans = null, GameYoloOption[]? finale = null)
    {
        if (act == 1)
        {
            return plans ?? [
                new("levitate", "Levitující šálek", "Přeletím nerovnou podlahu.", "Průvan může šálek shodit."),
                new("tray", "Pochodující podnos", "Čaj donese podnos, nemusím udržovat levitaci.", "Na nerovné podlaze může klopýtnout."),
                new("cup", "Šálek připravený u cíle", "Přenesu později jen čaj, cesta bude krátká.", "Soupeř může posunout přijímací šálek.")
            ];
        }
        if (act == 2)
        {
            var protection = own.FirstOrDefault()?.Guard ?? Plan(own) switch
            {
                "levitate" => "Zavřít okno",
                "tray" => "Vyrovnat cestu podnosu",
                _ => "Připevnit přijímací šálek"
            };
            var attack = opponent.FirstOrDefault()?.Attack ?? Plan(opponent) switch
            {
                "levitate" => "Poslat průvan na soupeřův šálek",
                "tray" => "Přistrčit překážku pod soupeřův podnos",
                _ => "Posunout soupeřův přijímací šálek"
            };
            return [
                new("protect", protection, "Pojistím svůj plán proti známé slabině i soupeřovu zásahu.", "Nepřipravím zkratku a soupeře nezdržím."),
                new("sabotage", attack, "Zkomplikuji soupeři jeho konkrétní plán.", "Moje vlastní slabina zůstane odkrytá; soupeř se může přizpůsobit."),
                new("shortcut", finale?.FirstOrDefault(option => option.Id == "risk") is { } risk ? $"Připravit: {risk.Title}" : "Připravit přenos přímo k cíli", "Připravím rychlé zakončení pro poslední dějství.", "Cíl nepojistím. Soupeřův zásah může přenos zmařit.")
            ];
        }
        if (finale is not null) { return finale; }
        var prepared = own.ElementAtOrDefault(1)?.Choice == "shortcut";
        return [
            new("finish", "Dokončit původní plán", "Pojistka z druhého dějství ochrání doručení.", "Bez pojistky plán selže na známé slabině."),
            new("adapt", "Doručit vlastníma rukama", "Obejdu slabinu plánu i soupeřův zásah a splním cíl.", "Doručení je pomalé. Připravená úspěšná zkratka má při hodnocení výhodu."),
            new("risk", "Použít připravenou zkratku", prepared ? "Využiju připravený přenos a splním cíl rychle." : "Pokusím se přeskočit celou cestu.",
                prepared ? "Pokud soupeř zasáhl můj nepojištěný plán, přenos mine cíl." : "Bez přípravy z druhého dějství cíl nesplním.")
        ];
    }

    internal static string Plan(IReadOnlyList<GameYoloDecision> decisions) => decisions.FirstOrDefault()?.Choice ?? "";

    internal static GameYoloDecision Select(int act, string? choice, IReadOnlyList<GameYoloDecision> own,
        IReadOnlyList<GameYoloDecision> opponent, GameYoloOption[]? plans = null, GameYoloOption[]? finale = null)
    {
        var option = Options(act, own, opponent, plans, finale).FirstOrDefault(option => option.Id == choice)
            ?? throw new GameFault(400, "game_yolo_choice_invalid");
        return new(act, option.Id, option.Title, option.Benefit, option.Cost, "", option.Guard, option.Attack);
    }

    internal static GameYoloDelivery Delivery(IReadOnlyList<GameYoloDecision> own, IReadOnlyList<GameYoloDecision> opponent, GameYoloSetting? setting = null)
    {
        var preparation = own.ElementAtOrDefault(1)?.Choice;
        var ending = own.ElementAtOrDefault(2)?.Choice;
        if (ending == "adapt")
        {
            return new(true, setting is null ? "Čaj dorazil na stůl v nepoškozeném šálku, pomalu a v ruce. Změna postupu obešla slabinu i případný zásah soupeře." : $"{setting.Receipt} Doručení v ruce obešlo známou slabinu i případný zásah soupeře, ale bylo pomalé.");
        }
        if (ending == "finish" && preparation == "protect")
        {
            return new(true, setting is null ? "Čaj dorazil na stůl v nepoškozeném šálku původním plánem, díky pojistce z druhého dějství." : $"{setting.Receipt} Původní plán uspěl díky pojistce z druhého dějství.");
        }
        if (ending == "risk" && preparation == "shortcut" && opponent.ElementAtOrDefault(1)?.Choice != "sabotage")
        {
            return new(true, setting is null ? "Čaj dorazil zkratkou na stůl v nepoškozeném šálku. Přenos byl připraven ve druhém dějství a soupeř jej nenarušil." : $"{setting.Receipt} Připravená zkratka uspěla, protože soupeř nenarušil její nepojištěný cíl.");
        }
        if (setting is not null)
        {
            return new(false, ending == "risk" ? preparation != "shortcut"
                ? "Cíl nesplněn: zkratka neměla přípravu z druhého dějství."
                : "Cíl nesplněn: soupeř narušil nepojištěný cíl připravené zkratky."
                : $"Cíl nesplněn: původní plán neměl pojistku. Známá cena: {own.FirstOrDefault()?.Cost}");
        }
        return new(false, ending == "risk"
            ? preparation != "shortcut" ? "Čaj skončil mimo šálek: přenos neměl přípravu z druhého dějství." : "Čaj skončil mimo šálek: soupeř narušil nepojištěný plán a připravená zkratka přišla o cíl."
            : "Čaj nebyl doručen v nepoškozeném šálku: původní plán neměl pojistku proti své známé slabině.");
    }

    internal static void Reveal(List<GameYoloDecision> left, List<GameYoloDecision> right, int act, GameYoloSetting? setting = null)
    {
        string Describe(IReadOnlyList<GameYoloDecision> own, IReadOnlyList<GameYoloDecision> opponent) => act switch
        {
            1 => $"Plán: {own[0].Title}. {own[0].Benefit} {own[0].Cost}",
            2 => own[1].Choice switch
            {
                "protect" => "Původní plán je pojištěný. Soupeřův zásah jej nezastaví; pro zkratku příprava chybí.",
                "sabotage" => opponent[1].Choice == "protect"
                    ? "Zásah narazil na soupeřovu pojistku. Tvůj plán zůstává bez pojistky i bez zkratky."
                    : "Soupeřův původní plán i případná zkratka jsou narušené. Stále může splnit cíl jinak. Tvůj plán zůstává bez pojistky.",
                _ => opponent[1].Choice == "sabotage"
                    ? "Přenos je připravený, ale soupeř narušil nepojištěný cíl. Ještě lze přejít na doručení v ruce."
                    : "Přenos je připravený a cíl zůstal na místě. Zkratku lze využít v posledním dějství."
            },
            _ => Delivery(own, opponent, setting).Consequence
        };
        var leftText = Describe(left, right);
        var rightText = Describe(right, left);
        left[act - 1] = left[act - 1] with { Consequence = leftText };
        right[act - 1] = right[act - 1] with { Consequence = rightText };
    }
}

internal sealed partial class GameStore
{
    private static void ChooseYolo(State state, string userId, GameCommand request)
    {
        if (state.Status != "active" || state.YoloScenarioId is null)
        {
            throw new GameFault(409, "game_action_unavailable");
        }
        if (request.SparkBid != 0) { throw new GameFault(400, "spark_bid_invalid"); }
        var act = state.YoloCompletedActs + 1;
        var own = userId == state.ChallengerId ? state.ChallengerYoloDecisions : state.OpponentYoloDecisions;
        var opponent = userId == state.ChallengerId ? state.OpponentYoloDecisions : state.ChallengerYoloDecisions;
        if (act > 3 || own.Count >= act) { throw new GameFault(409, "choice_locked"); }
        // Only previously revealed opponent decisions may affect offered choices.
        var plans = userId == state.ChallengerId ? state.YoloSetting?.ChallengerPlans : state.YoloSetting?.OpponentPlans;
        var finale = userId == state.ChallengerId ? state.YoloSetting?.ChallengerFinale : state.YoloSetting?.OpponentFinale;
        own.Add(GameYoloScenario.Select(act, request.Choice, own, opponent.Take(state.YoloCompletedActs).ToArray(), plans, finale));
        if (opponent.Count < act) { return; }
        GameYoloScenario.Reveal(state.ChallengerYoloDecisions, state.OpponentYoloDecisions, act, state.YoloSetting);
        state.YoloCompletedActs++;
        if (act == 3)
        {
            if (state.ChallengerPublicProfile is not null)
            {
                state.ChallengerPublicProfile = state.ChallengerPublicProfile with { Decisions = state.ChallengerYoloDecisions.ToArray() };
            }
            if (state.OpponentPublicProfile is not null)
            {
                state.OpponentPublicProfile = state.OpponentPublicProfile with { Decisions = state.OpponentYoloDecisions.ToArray() };
            }
            state.Status = "generating";
            state.YoloStatus = "generating";
            state.YoloGenerationRevision = state.Revision + 1;
        }
    }

    private static GameYoloScenarioView? YoloScenarioView(State state, bool challenger)
    {
        if (state.YoloScenarioId is null) { return null; }
        var own = challenger ? state.ChallengerYoloDecisions : state.OpponentYoloDecisions;
        var opponent = challenger ? state.OpponentYoloDecisions : state.ChallengerYoloDecisions;
        var act = Math.Min(3, state.YoloCompletedActs + 1);
        return new(state.YoloScenarioId, state.YoloSetting?.Title ?? GameYoloScenario.Title, state.YoloSetting?.Objective ?? GameYoloScenario.Objective, state.YoloSetting?.Situation ?? GameYoloScenario.Situation,
            act, state.Status == "active" && own.Count > state.YoloCompletedActs,
            state.Status == "active" && opponent.Count > state.YoloCompletedActs,
            state.Status == "active" ? GameYoloScenario.Options(act, own.Take(state.YoloCompletedActs).ToArray(), opponent.Take(state.YoloCompletedActs).ToArray(), challenger ? state.YoloSetting?.ChallengerPlans : state.YoloSetting?.OpponentPlans,
                challenger ? state.YoloSetting?.ChallengerFinale : state.YoloSetting?.OpponentFinale) : [],
            Enumerable.Range(0, state.YoloCompletedActs).Select(index => new GameYoloAct(index + 1, own[index], opponent[index])).ToArray(), state.YoloSetting?.SelectionReason);
    }
}
