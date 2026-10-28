namespace Kufr.Backend.Features.Game;

internal sealed partial class GameStore
{
    internal IReadOnlyList<(string Id, string ChallengerId)> IncomingPushChallenges(string groupId, string userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,challenger_id FROM game_matches
            WHERE group_id=$group AND opponent_id=$user
                AND json_extract(state_json,'$.status')='incoming'
            ORDER BY created_at DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read()) { result.Add((reader.GetString(0), reader.GetString(1))); }
        return result;
    }
}
