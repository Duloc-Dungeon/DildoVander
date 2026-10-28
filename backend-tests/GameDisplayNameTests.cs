using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;

internal static class GameDisplayNameTests
{
    internal static void Run()
    {
        var labeled = Profile("u1", "Merlin", "Čaroděj");
        var unlabeled = Profile("u2", "Morgana", null);
        Require(GameDisplayNames.DisplayName("Merlin", labeled) == "Merlin · Čaroděj",
            "profile label appears in a game projection");
        Require(GameDisplayNames.DisplayName("Morgana", unlabeled) == "Morgana",
            "unlabeled profile preserves account display name");
        Require(GameDisplayNames.DisplayName("Account name", null) == "Account name",
            "missing profile preserves account display name");
        Require(labeled.UserInput.Name == "Merlin",
            "label does not alter the original character name");

        var opponent = new GameOpponentView("u1", "Merlin", 8, 3);
        var match = Match(opponent);
        var lobby = new GameLobby(
            new GamePlayerView("u2", "Morgana", 5, 2, true, false),
            [opponent], [match], [match], [match]);
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["u1"] = "Merlin · Čaroděj",
            ["u2"] = "Morgana · Oracle"
        };

        var decoratedLobby = GameDisplayNames.Decorate(lobby, names);
        Require(decoratedLobby.Player.DisplayName == "Morgana · Oracle",
            "lobby player uses label");
        Require(decoratedLobby.Opponents[0].DisplayName == "Merlin · Čaroděj",
            "lobby opponent uses label");
        Require(decoratedLobby.Incoming[0].Opponent.DisplayName == "Merlin · Čaroděj"
            && decoratedLobby.Active[0].Opponent.DisplayName == "Merlin · Čaroděj"
            && decoratedLobby.History[0].Opponent.DisplayName == "Merlin · Čaroděj",
            "incoming, active and historical matches use label");
        Require(lobby.Active[0].Opponent.DisplayName == "Merlin",
            "stored match view remains unchanged");

        var progress = new GameProgressView(1, 0, "available", [], [],
            [new GameRanking("u1", "Merlin", 8, 0, 1)],
            [new GameRanking("u1", "Merlin", 0, 2, 1)], []);
        var decoratedProgress = GameDisplayNames.Decorate(progress, names);
        Require(decoratedProgress.CombatRanking[0].DisplayName == "Merlin · Čaroděj"
            && decoratedProgress.YoloRanking[0].DisplayName == "Merlin · Čaroděj",
            "progress rankings use label");
        Require(progress.CombatRanking[0].DisplayName == "Merlin",
            "stored progress view remains unchanged");
    }

    private static CharacterProfile Profile(string id, string name, string? label) => new(
        id, 1, new CharacterUserInput(name, "world"), "ready", null, null, 1) { Label = label };

    private static GameMatchView Match(GameOpponentView opponent) => new(
        "match", "tactical", "active", 1, false, false, opponent, [], 0,
        0, 0, false, 0, 0, 1, new Dictionary<string, int>(), new Dictionary<string, int>(),
        false, false, 1, null, false, false, false, null, 1, null, null, 0);

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Failed: {name}");
        }
    }
}
