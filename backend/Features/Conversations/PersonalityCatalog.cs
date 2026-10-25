using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.Conversations;

internal sealed record PersonalityInfo(string Id, string Name, string Description, string PromptSha256);

internal static class PersonalityCatalog
{
    internal const string DefaultId = "dv01";
    internal const string LegacyPartyOriginalId = "party-original";
    internal const string LegacyOllivanderId = "ollivander-trunk";
    // Kept as an alias for source compatibility in dormant Realtime recovery code.
    internal const string OllivanderId = LegacyOllivanderId;

    private static readonly string LegacyPartyOriginal = PartyDialoguePrompt.VoicePersona;
    private static readonly string LegacyOllivander = PartyDialoguePrompt.ReadResource("Kufr.Backend.OllivanderPersona", trim: false);
    private static readonly IReadOnlyList<PersonalityInfo> inventory = Array.AsReadOnly(new[]
    {
        Info(DefaultId, "DV01", "The single Wand Night Stand character. Adult and Chaos are behavior variations.", CharacterConfiguration.Persona)
    });

    internal static IReadOnlyList<PersonalityInfo> Inventory => inventory;
    internal static bool Contains(string id) => id == DefaultId;

    internal static bool IsHistorical(string id) => id is LegacyPartyOriginalId or LegacyOllivanderId;

    internal static string Persona(string id) => id == DefaultId
        ? CharacterConfiguration.Persona
        : throw new ArgumentException("Historical personalities cannot be activated.", nameof(id));

    internal static string DecisionInstructions(string id) => Persona(id) + "\n\n" + PartyDialoguePrompt.SelectionInstructions;

    internal static string AutonomousLivePersona(string id) => id == DefaultId
        ? CharacterConfiguration.Persona
        : throw new ArgumentException("Historical personalities cannot be activated.", nameof(id));

    internal static int MinimumQuestions(string id) => 2;
    internal static int FakeQuestions(string id) => 2;

    internal static string OpeningInstructions(string id, string variation) => id == DefaultId
        ? ConversationOpening.Instructions(variation)
        : throw new ArgumentException("Historical personalities cannot be activated.", nameof(id));

    internal static int ExtraRequestBytes(string id)
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return Math.Max(0, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(DecisionInstructions(id), options))
            - Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(PartyDialoguePrompt.DecisionInstructions, options)));
    }

    // Read-only access keeps archived prompt references available to diagnostics and migration tools.
    internal static string HistoricalPrompt(string id) => id switch
    {
        LegacyPartyOriginalId => LegacyPartyOriginal,
        LegacyOllivanderId => LegacyOllivander,
        _ => throw new ArgumentException("Unknown historical personality.", nameof(id))
    };

    internal static string HistoricalAutonomousLivePrompt(string id) => id switch
    {
        LegacyPartyOriginalId => LegacyPartyOriginal.Replace(
            "Typicky 4–7 otázek včetně úvodní. Serverová brána určuje, kdy je výběr povolený. Po splnění brány zakonči, jakmile máš materiál; nevynucuj sedm kol.",
            "Důvěryhodná provozní politika určuje rytmus; vlastní otázky a roast tvoříš sám. Po splnění politiky směřuj k ověřenému výběru.", StringComparison.Ordinal)
            + "\nDelegation policy:\nBackend tools: Výběr skutečné hůlky, dostupnost, povolená paměť a platnost operace. "
            + "Delegate to the backend when: Je čas vybrat; potřebuješ neznámou dostupnost nebo vzpomínku; host opravuje výběr, "
            + "žádá alternativu či změnu nebo zrušení operace. Do not delegate to the backend when: Pozdrav, roast, běžná "
            + "reakce, vlastní navazující otázka, rozvíjení odpovědi, poděkování nebo zopakování známé informace. "
            + "Backend neposkytuje hotovou repliku. Ověřený výsledek oznam vlastními slovy; žádný výběr ani fyzickou akci si nevymýšlej.",
        LegacyOllivanderId => PartyDialoguePrompt.ReadResource("Kufr.Backend.OllivanderLivePersona"),
        _ => throw new ArgumentException("Unknown historical personality.", nameof(id))
    };

    private static PersonalityInfo Info(string id, string name, string description, string prompt) =>
        new(id, name, description, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))));
}
