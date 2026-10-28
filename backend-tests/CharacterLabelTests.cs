using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

internal static class CharacterLabelTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-character-labels-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CharacterProfileStore(root);
            var first = await store.SaveAsync("alice", new(0, new("Mozkomor", "Harry Potter"), OpenAiConsent: true));
            var second = await store.SaveAsync("bob", new(0, first.UserInput, OpenAiConsent: true));
            var labeled = await store.WriteLabelAsync("alice", new(first.ProfileId, "  Petr  "));
            var other = await store.WriteLabelAsync("bob", new(second.ProfileId, "Jana"));
            Check(labeled.DisplayName == "Mozkomor · Petr" && other.DisplayName == "Mozkomor · Jana",
                "same names have independent owner-scoped display labels");
            Check(labeled.Revision == first.Revision && labeled.UserInput == first.UserInput,
                "display label never revises or changes the AI input");
            var restarted = new CharacterProfileStore(root);
            Check((await restarted.ReadAsync("alice"))?.Label == "Petr", "labels survive restart");
            var snapshot = await restarted.CaptureAsync("alice", first.ProfileId, first.Revision);
            Check(snapshot?.UserInput.Name == "Mozkomor"
                && !JsonSerializer.Serialize(snapshot).Contains("Petr", StringComparison.Ordinal),
                "AI conversation snapshot excludes the display label");
            var job = await restarted.ClaimAsync();
            Check(job is not null && job.UserInput.Name == "Mozkomor"
                && !JsonSerializer.Serialize(job).Contains("Petr", StringComparison.Ordinal),
                "already queued provider input excludes labels");
            await ExpectAsync("character_label_conflict", () => store.WriteLabelAsync("alice", new(first.ProfileId, "New")));
            await ExpectAsync("character_label_conflict", () => store.WriteLabelAsync("bob", new(first.ProfileId, "Stolen", "Jana")));
            foreach (var invalid in new[] { new string('a', 81), "Petr\nDruhý", "Petr\tDruhý" })
            {
                await ExpectAsync("invalid_character_label", () => store.WriteLabelAsync("alice", new(first.ProfileId, invalid, "Petr")));
            }
            var edited = await store.SaveAsync("alice", new(first.Revision,
                first.UserInput with { Version = "Film" }, first.ProfileId, OpenAiConsent: true));
            Check(edited.Label == "Petr", "profile edits preserve the independent label");
            var cleared = await store.WriteLabelAsync("alice", new(first.ProfileId, " ", "Petr"));
            Check(cleared.Label is null && cleared.DisplayName == "Mozkomor", "empty label restores original display");
            await store.WriteLabelAsync("alice", new(first.ProfileId, "Petr"));
            await store.DeleteAsync("alice", edited.Revision, edited.ProfileId);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = store.CustomerDataDatabasePath }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM character_labels WHERE account_id='alice'";
            Check((long)query.ExecuteScalar()! == 0, "profile deletion removes its label");
            Console.WriteLine("Character display labels persist without changing AI input, revisions or ownership.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private static async Task ExpectAsync(string code, Func<Task<CharacterProfile>> action)
    {
        try { await action(); }
        catch (CharacterException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected character label error " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Character labels: " + message); }
    }
}
