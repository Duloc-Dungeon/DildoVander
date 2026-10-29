using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Wands;

internal sealed partial class WandGenerationStore
{
    private void CaptureSelectedWandChronicle(SqliteConnection connection, SqliteTransaction transaction,
        string accountId, string operationId, string sessionId, GeneratedWandSnapshot wand)
    {
        using var query = Command(connection, transaction, """
            SELECT generation.request_json,COALESCE(workshop.tier,1),COALESCE(workshop.launch_group_id,'')
            FROM wand_generation_sessions generation
            LEFT JOIN mobile_wand_workshops workshop ON workshop.account_id=generation.account_id
                AND workshop.operation_id=generation.operation_id
            WHERE generation.id=$id
            """, ("$id", sessionId));
        string? requestJson;
        int tier;
        string groupId;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(0)) { return; }
            requestJson = reader.GetString(0);
            tier = reader.GetInt32(1);
            groupId = reader.GetString(2);
        }
        var request = JsonSerializer.Deserialize<WandGenerationRequest>(requestJson, Json)!;
        if (string.IsNullOrEmpty(groupId)) { groupId = "account:" + accountId; }
        var profile = request.Profile;
        var previous = new List<string>();
        if (tier > 1)
        {
            using var prior = Command(connection, transaction, """
                SELECT selected_json FROM wand_generation_sessions
                WHERE account_id=$owner AND id<>$id AND selected_json IS NOT NULL
                ORDER BY rowid DESC LIMIT 1
                """, ("$owner", accountId), ("$id", sessionId));
            if (prior.ExecuteScalar() is string json && JsonSerializer.Deserialize<GeneratedWandSnapshot>(json, Json) is { } original)
            {
                previous.Add($"Původní hůlka: {original.Name}. {original.DesignDescription}");
            }
        }
        var title = tier > 1 ? "Druhé vydání hůlky" : "Moje první hůlka";
        var participant = new ChronicleParticipant(accountId, profile.UserInput.Name, "owner",
            profile.UserInput.Name, wand.Name,
            JsonSerializer.Serialize(new { character = new { profile.UserInput.Name, profile.UserInput.World, profile.UserInput.Version }, wand = wand.ToPublic() }, Json),
            $"character:{profile.ProfileId}:{profile.Revision};wand:{wand.WandId}");
        var snapshot = new ChronicleEventInput("wand", wand.WandId, groupId, Now, title,
            wand.Lore[..Math.Min(wand.Lore.Length, 1000)], [participant],
            Story: previous.Append($"{wand.Name}. {wand.DesignDescription}").Append(wand.Lore).ToArray(),
            ShareText: $"{profile.UserInput.Name} · {wand.Name}", IllustrationRequested: true, Visibility: "public");
        ChronicleAward[] awards = tier > 1
            ? [new("second_edition", "Druhé vydání", "account", "Dokončený upgrade hůlky; " + wand.WandId)]
            : [];
        ChronicleSourceOutbox.Append(connection, transaction, new(snapshot, [accountId],
            new Dictionary<string, IReadOnlyList<ChronicleAward>> { [accountId] = awards }));
    }

    internal int DrainWandChronicle(ChronicleStore destination)
    {
        using var connection = Open();
        return ChronicleSourceOutbox.Drain(connection, destination);
    }

    internal string SaveWorkshopMoment(string accountId, string operationId, string messageId, int expectedRevision)
    {
        using var connection = Open();
        EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        RequireNotRevoked(connection, transaction, accountId, row.ProfileId);
        if (row.Status != "complete") { throw new WandGenerationException("wand_conversation_not_finished", 409); }
        var message = Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson)
            .SingleOrDefault(item => item.MessageId == messageId && item.Role == "assistant");
        if (message is null || message.Revision != expectedRevision)
        { throw new WandGenerationException("wand_conversation_changed", 409); }
        var profile = Deserialize<Kufr.Backend.Features.Customers.CharacterConversationSnapshot>(row.ProfileJson);
        var sourceId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(accountId + ":" + operationId + ":" + messageId + ":" + message.Revision)));
        var participant = new ChronicleParticipant(accountId, profile.UserInput.Name, "owner", profile.UserInput.Name,
            VisualSnapshotJson: JsonSerializer.Serialize(new { profile.UserInput.Name, profile.UserInput.World, profile.UserInput.Version }, Json),
            VisualReferenceVersion: $"character:{profile.ProfileId}:{profile.Revision}");
        var snapshot = new ChronicleEventInput("conversation", sourceId,
            string.IsNullOrEmpty(row.LaunchGroupId) ? "account:" + accountId : row.LaunchGroupId,
            message.CreatedAt / 1000, "Vzpomínka z dílny", message.Text[..Math.Min(1000, message.Text.Length)], [participant],
            Story: [message.Text], IllustrationRequested: true, Visibility: "private");
        ChronicleSourceOutbox.Append(connection, transaction,
            new(snapshot, [accountId], new Dictionary<string, IReadOnlyList<ChronicleAward>>()));
        transaction.Commit();
        return sourceId;
    }
}
