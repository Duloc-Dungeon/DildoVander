using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed partial class SealedCaseStore
{
    // A server sweep observes the released finale; gallery reads never issue work.
    // Keyset rotation lets unfinished cases coexist with later completed cases.
    internal long CaptureReleasedChronicles(long afterRowId = 0)
    {
        using var connection = Open();
        ChronicleSourceOutbox.Ensure(connection);
        var candidates = new List<(long RowId, string Id, string GroupId, long Revision)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT cases.rowid,cases.id,cases.group_id,cases.revision
                FROM sealed_cases cases JOIN sealed_case_timing timing ON timing.case_id=cases.id
                WHERE cases.rowid>$after AND cases.status<>'privacy_terminated'
                    AND NOT EXISTS(SELECT 1 FROM chronicle_source_outbox outbox WHERE outbox.source_key='case:'||cases.id)
                ORDER BY cases.rowid LIMIT 32
                """;
            query.Parameters.AddWithValue("$after", afterRowId);
            using var reader = query.ExecuteReader();
            while (reader.Read()) { candidates.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))); }
        }
        foreach (var candidate in candidates)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                using var admission = AcquireCaseAccess(connection, transaction, candidate.Id);
                var timing = ReadTiming(connection, transaction, candidate.Id, candidate.Revision, clock.GetUtcNow().ToUnixTimeSeconds());
                if (timing.Phase != "finished") { continue; }
                var members = RosterMembers(connection, transaction, candidate.Id);
                var finale = ReadFinale(connection, transaction, candidate.Id, members, "", timing);
                if (finale is not { Phase: "released", Reconstruction: { } recap }) { continue; }
                var participants = ReadChronicleParticipants(connection, transaction, candidate.Id, members);
                var owners = members.Where(member => !member.IsAi).Select(member => member.AccountId).ToArray();
                if (owners.Length == 0) { continue; }
                using var query = connection.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = "SELECT setup_json FROM sealed_cases WHERE id=$case";
                query.Parameters.AddWithValue("$case", candidate.Id);
                using var setup = JsonDocument.Parse((string)query.ExecuteScalar()!);
                var presentation = SealedScenarioCatalog.FromSetup(setup.RootElement);
                var actors = members.ToDictionary(member => member.PlayerId, member => member.AccountId, StringComparer.Ordinal);
                var decisions = recap.Select(item => new ChronicleDecision(actors.GetValueOrDefault(item.Actor, item.Actor),
                    $"Kapitola {item.Chapter}: {ChronicleIntent(item.Intent)}", item.Observation ?? item.Reason ?? ChronicleResult(item.Result),
                    $"{item.EventRef}; výsledek: {ChronicleResult(item.Result)}" )).ToArray();
                var title = "Pod pečetí · " + presentation.Describe().Title;
                var snapshot = new ChronicleEventInput("case", candidate.Id, candidate.GroupId, timing.FinaleEndsAt,
                    title, finale.Message ?? "Případ je uzavřený. Rekonstrukce je uvolněná.", participants,
                    Result: finale.Winner, Story: recap.Select(item =>
                        $"Kapitola {item.Chapter} · {participants.FirstOrDefault(person => person.AccountId == actors.GetValueOrDefault(item.Actor))?.DisplayName ?? item.Actor}: {ChronicleIntent(item.Intent)} — {item.Observation ?? item.Reason ?? ChronicleResult(item.Result)}").ToArray(),
                    Decisions: decisions, ShareText: title + ". " + finale.Message,
                    IllustrationRequested: true, Visibility: "public");
                var awards = owners.ToDictionary(owner => owner,
                    _ => (IReadOnlyList<ChronicleAward>)[new ChronicleAward("seal_released", "Pečeť povolila", "account", "Dokončený případ s uvolněnou rekonstrukcí; " + candidate.Id)], StringComparer.Ordinal);
                ChronicleSourceOutbox.Append(connection, transaction, new(snapshot, owners, awards));
                transaction.Commit();
            }
            catch (SealedCasePersistenceException error) when (error.Status == 404)
            {
                // A privacy reservation can invalidate a candidate during the sweep.
            }
        }
        return candidates.Count == 32 ? candidates[^1].RowId : 0;
    }

    private static ChronicleParticipant[] ReadChronicleParticipants(SqliteConnection connection,
        SqliteTransaction transaction, string caseId, SealedStoredRosterMember[] members)
    {
        var profiles = new Dictionary<string, (CharacterProfile Profile, byte[]? Image)>(StringComparer.Ordinal);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT subject_account,profile_json,image FROM sealed_case_private_snapshots WHERE case_id=$case";
        query.Parameters.AddWithValue("$case", caseId);
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                profiles[reader.GetString(0)] = (JsonSerializer.Deserialize<CharacterProfile>(reader.GetString(1), Json)!,
                    reader.IsDBNull(2) ? null : ChronicleReferenceImage.CopyBounded((byte[])reader[2]));
            }
        }
        return members.Select(member =>
        {
            profiles.TryGetValue(member.AccountId, out var frozen);
            var profile = frozen.Profile;
            var portrait = frozen.Image;
            var portraitSha = portrait is null ? "none" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(portrait));
            var name = profile?.UserInput.Name ?? member.DisplayName ?? member.PlayerId;
            // No allegiance, raw character input, plan, ballot or private note
            // enters the illustration/share projection.
            return new ChronicleParticipant(member.AccountId, name, "participant", name,
                VisualSnapshotJson: JsonSerializer.Serialize(new
                {
                    name, world = profile?.UserInput.World, kit = member.Kit.ToString(),
                    portraitReferenceBase64 = portrait is null ? null : Convert.ToBase64String(portrait),
                    portraitSha256 = portraitSha, portraitMediaType = portrait is null ? null : "image/png"
                }, Json),
                VisualReferenceVersion: $"case:{caseId};character:{member.ProfileId}:{member.ProfileRevision};portrait:{portraitSha}");
        }).ToArray();
    }

    internal int DrainCaseChronicle(ChronicleStore destination)
    {
        using var connection = Open();
        return ChronicleSourceOutbox.Drain(connection, destination);
    }

    private static string ChronicleIntent(string intent) => intent switch
    {
        "Hold" => "Vyčkání", "Rest" => "Odpočinek", "Guard" => "Ochrana", "Prepare" => "Příprava",
        "Investigate" => "Pátrání", "Follow" => "Sledování", "Steal" => "Krádež", "Sabotage" => "Sabotáž",
        "Stun" => "Omráčení", "Lethal" => "Smrtící útok", "Help" => "Pomoc", "EchoReview" => "Prohlédnutí ozvěny",
        "EchoDeliver" => "Doručení ozvěny", "EchoScene" => "Ozvěna místa", "Dream" => "Sen", "Mask" => "Maskování",
        "FeignSleep" => "Předstíraný spánek", "Transfer" => "Předání", _ => "Uložená akce"
    };

    private static string ChronicleResult(string result) => result switch
    {
        "Success" => "Úspěch", "Defended" => "Odraženo", "Hold" => "Vyčkání", "RequestValid" => "Platný požadavek",
        _ => "Uložený výsledek"
    };
}
