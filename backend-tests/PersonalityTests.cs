using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class PersonalityTests
{
    internal static async Task RunAsync()
    {
        Require(PersonalityCatalog.DefaultId == "dv01", "DV01 is the sole production identity");
        Require(PersonalityCatalog.Inventory.Count == 1
            && PersonalityCatalog.Inventory[0].Id == PersonalityCatalog.DefaultId,
            "only DV01 is advertised as a selectable suitcase personality");
        Require(!PersonalityCatalog.Contains(PersonalityCatalog.LegacyPartyOriginalId)
            && !PersonalityCatalog.Contains(PersonalityCatalog.LegacyOllivanderId),
            "historical identities cannot pass the production personality guard");
        Require(PersonalityCatalog.HistoricalPrompt(PersonalityCatalog.LegacyPartyOriginalId).Length > 0
            && PersonalityCatalog.HistoricalPrompt(PersonalityCatalog.LegacyOllivanderId).Length > 0
            && PersonalityCatalog.HistoricalAutonomousLivePrompt(PersonalityCatalog.LegacyOllivanderId).Length > 0,
            "archived source prompts remain available for historical diagnostics");
        Require(PersonalityCatalog.Persona(PersonalityCatalog.DefaultId) == CharacterConfiguration.Persona,
            "the production catalog delegates to the canonical DV01 specification");
        Require(!LabContract.Engine(RealtimeProtocol.Model) && LabContract.Engine(GptLiveProtocol.Model),
            "Persona Lab accepts only new GPT-Live sessions");

        VerifyLegacySettingIsIgnoredAndPreserved();
        await VerifyRealtimeStartIsRetiredAsync();
        Console.WriteLine("DV01 sole-personality, legacy-setting, and engine capability tests passed.");
    }


    private static async Task VerifyRealtimeStartIsRetiredAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapRealtimeLive();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync($"/v1/conversations/{Guid.NewGuid()}/realtime-live", null);
            var body = await response.Content.ReadAsStringAsync();
            Require((int)response.StatusCode == 410 && body.Contains("conversation_engine_upgrade_required", StringComparison.Ordinal),
                "production Realtime start API returns an explicit upgrade response");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static void VerifyLegacySettingIsIgnoredAndPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-personalities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "journal.sqlite");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = databasePath
        }).Build();
        try
        {
            using (var journal = new OperationJournal(configuration, TimeProvider.System))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO server_settings(name,value) VALUES('personality',$value)";
                command.Parameters.AddWithValue("$value", PersonalityCatalog.LegacyOllivanderId);
                command.ExecuteNonQuery();

                Require(journal.ReadPersonality() == PersonalityCatalog.DefaultId,
                    "a persisted historical personality never overrides new DV01 sessions");
                var store = new ConversationStore(TimeProvider.System, journal);
                var modes = store.ReadServerModes();
                Require(modes.PersonalityId == PersonalityCatalog.DefaultId,
                    "new conversation configuration resolves to DV01");
                Require(modes.Engines?.PrimaryEngine == "gpt-live"
                    && modes.Engines.Engines.Count == 1
                    && modes.Engines.Engines[0].Engine == "gpt-live",
                    "capabilities expose GPT-Live as the only active dialogue engine");

                var denied = store.ChangeServerModes(new(modes.Revision,
                    PersonalityId: PersonalityCatalog.LegacyOllivanderId));
                Require(denied.Status == 409 && denied.Error == "only_dv01_personality_supported",
                    "the admin API rejects historical personality activation");

                command.CommandText = "SELECT value FROM server_settings WHERE name='personality'";
                command.Parameters.Clear();
                Require(command.ExecuteScalar() as string == PersonalityCatalog.LegacyOllivanderId,
                    "legacy persisted configuration is left intact for historical reads");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }
}
