using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed record CaseVoiceAsideReceipt(string EveningId, int SessionNumber, int MissesBefore,
    int RollDenominator, bool Selected, string? PromptInstruction);

internal sealed partial class EveningMemoryStore
{
    private const int CaseAsideCadenceSessions = 10;
    private const string CaseAsideInstruction = "Jednorázová úvodní vsuvka této relace: v čistě fikční dílně je Olivander dnes opilý jak dělo a příliš opilý na prodej hůlek, takže prodej přebírá kouzelný kufr. Zapracuj jednu krátkou, původní suchou britskou větu do úvodu. Kufr nikdy nenapodobuje Olivandera. Opilost se týká jen fikčního hostitele, nikdy návštěvníka. Vsuvku neopakuj při obnově ani v dalších kolech a netvrď, že byla slyšena nebo ověřena.";

    private static void MigrateCaseVoiceAsides(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS case_voice_aside_cadence (
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                missed_sessions INTEGER NOT NULL CHECK(missed_sessions BETWEEN 0 AND 9),
                total_sessions INTEGER NOT NULL CHECK(total_sessions >= 0),
                selected_count INTEGER NOT NULL CHECK(selected_count >= 0),
                PRIMARY KEY(device_id, evening_id));
            CREATE TABLE IF NOT EXISTS case_voice_aside_assignments (
                device_id TEXT NOT NULL, conversation_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                session_number INTEGER NOT NULL, misses_before INTEGER NOT NULL,
                roll_denominator INTEGER NOT NULL, selected INTEGER NOT NULL,
                prompt_instruction TEXT NULL,
                PRIMARY KEY(device_id, conversation_id));
            CREATE INDEX IF NOT EXISTS case_voice_aside_evening
                ON case_voice_aside_assignments(device_id, evening_id, session_number);
            """);
        command.ExecuteNonQuery();
    }

    // Counts only an already captured show visit. The row is both the stable
    // session key and the idempotency receipt across retries and restarts.
    internal CaseVoiceAsideReceipt? AssignCaseVoiceAside(string deviceId, string conversationId,
        Func<int, int>? nextRoll = null)
    {
        if (!ValidShowIdentifier(deviceId) || !ValidShowIdentifier(conversationId)) { return null; }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var captured = Command(connection, transaction,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                ("$device", deviceId), ("$conversation", conversationId));
            if (captured.ExecuteScalar() is not string eveningId
                || ShowSourceRevoked(connection, transaction, deviceId, conversationId)
                || ShowClosed(connection, transaction, deviceId, eveningId)
                || ShowMemoryScopeDeleted(connection, transaction, deviceId, eveningId, conversationId))
            { return null; }

            using var existing = Command(connection, transaction, """
                SELECT evening_id,session_number,misses_before,roll_denominator,selected,prompt_instruction
                FROM case_voice_aside_assignments WHERE device_id=$device AND conversation_id=$conversation;
                """, ("$device", deviceId), ("$conversation", conversationId));
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != eveningId) { return null; }
                    var receipt = ReadReceipt(reader);
                    transaction.Commit();
                    return receipt;
                }
            }

            using var state = Command(connection, transaction, """
                SELECT missed_sessions,total_sessions,selected_count
                FROM case_voice_aside_cadence WHERE device_id=$device AND evening_id=$evening;
                """, ("$device", deviceId), ("$evening", eveningId));
            (int misses, int total, int selectedCount) stateValues;
            using (var stateReader = state.ExecuteReader()) { stateValues = ReadState(stateReader); }
            var (misses, total, selectedCount) = stateValues;
            var denominator = CaseAsideCadenceSessions - misses;
            var roll = nextRoll is null ? RandomNumberGenerator.GetInt32(denominator) : nextRoll(denominator);
            if (roll < 0 || roll >= denominator) { throw new InvalidOperationException("Aside cadence roll is out of range."); }
            var selected = roll == 0;
            var sessionNumber = checked(total + 1);
            var prompt = selected ? CaseAsideInstruction : null;
            using var insert = Command(connection, transaction, """
                INSERT INTO case_voice_aside_assignments
                    (device_id,conversation_id,evening_id,session_number,misses_before,roll_denominator,selected,prompt_instruction)
                VALUES($device,$conversation,$evening,$session,$misses,$denominator,$selected,$prompt);
                """, ("$device", deviceId), ("$conversation", conversationId), ("$evening", eveningId),
                ("$session", sessionNumber), ("$misses", misses), ("$denominator", denominator),
                ("$selected", selected ? 1 : 0), ("$prompt", (object?)prompt ?? DBNull.Value));
            insert.ExecuteNonQuery();
            using var update = Command(connection, transaction, """
                INSERT INTO case_voice_aside_cadence(device_id,evening_id,missed_sessions,total_sessions,selected_count)
                VALUES($device,$evening,$nextMisses,$total,$selectedCount)
                ON CONFLICT(device_id,evening_id) DO UPDATE SET missed_sessions=$nextMisses,
                    total_sessions=$total,selected_count=$selectedCount;
                """, ("$device", deviceId), ("$evening", eveningId),
                ("$nextMisses", selected ? 0 : misses + 1), ("$total", sessionNumber),
                ("$selectedCount", selectedCount + (selected ? 1 : 0)));
            update.ExecuteNonQuery();
            transaction.Commit();
            return new(eveningId, sessionNumber, misses, denominator, selected, prompt);
        }
    }

    internal (int MissedSessions, int TotalSessions, int SelectedCount) CaseVoiceAsideCadence(
        string deviceId, string eveningId)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT missed_sessions,total_sessions,selected_count FROM case_voice_aside_cadence
            WHERE device_id=$device AND evening_id=$evening;
            """, ("$device", deviceId), ("$evening", eveningId));
        using var reader = command.ExecuteReader();
        return ReadState(reader);
    }

    private static CaseVoiceAsideReceipt ReadReceipt(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
        reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static (int MissedSessions, int TotalSessions, int SelectedCount) ReadState(SqliteDataReader reader) =>
        reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : (0, 0, 0);

    private static void DeleteCaseVoiceAsideHistory(SqliteConnection connection, SqliteTransaction transaction,
        string deviceId, string eveningId)
    {
        using var command = Command(connection, transaction, """
            DELETE FROM case_voice_aside_assignments WHERE device_id=$device AND evening_id=$evening;
            DELETE FROM case_voice_aside_cadence WHERE device_id=$device AND evening_id=$evening;
            """, ("$device", deviceId), ("$evening", eveningId));
        command.ExecuteNonQuery();
    }
}
