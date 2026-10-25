using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.Realtime;

// Both voice engines consume the same revision/operation-bound candidate set.
internal static class CustomerWandSelection
{
    // Digital keepsakes are reusable choices, not consumable physical stock.
    internal const int KeepsakeReadyMask = 63;
    private const string KeepsakeInstructions = "Všech šest digitálních hůlkových památek je vždy dostupných, i v produkci. "
        + "Kufr vydává digitální památku, nikoli fyzickou hůlku; předchozí výběry nabídku nevyčerpávají. ";

    internal static async Task<RealtimeCall> PrepareAsync(RealtimeCall call, ConversationStore conversations,
        IServiceProvider services, CancellationToken token)
    {
        if (call.LabHardware is not null) return call;
        var journal = services.GetRequiredService<OperationJournal>();
        var session = journal.CustomerConversation(call.Correlation.DeviceId, call.Correlation.ConversationId);
        if (session is null) return call; // Operator/legacy conversations have no authenticated character.
        var captured = JsonSerializer.Deserialize<CustomerConversationSnapshot>(session.Snapshot,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new WandGenerationException("character_snapshot_unavailable");
        var store = services.GetRequiredService<WandGenerationStore>();
        // Admission captured this profile before conversation startup. Ordinary edits apply to
        // the next session; deletion and delegated-account revocation always win.
        var existing = store.Read(session.OwnerId, session.OperationId);
        if (existing is null)
            store.Begin(session.OwnerId, session.OperationId, captured.Profile, KeepsakeReadyMask,
                captured.OpenAiConsent);
        var memory = services.GetRequiredService<Kufr.Backend.Features.Memory.HybridMemoryService>();
        var memoryAccess = conversations.LiveMemoryAccess(call);
        var factEpoch = memoryAccess is null ? 0 : memory.StoreEpochFor(memoryAccess);
        var evidence = captured.PersonalMemoryConsent && memoryAccess is not null ? memory.PermittedWandEvidence(memoryAccess) : [];
        Task<bool> Authority(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var guard = services.GetRequiredService<CustomerAdmissionAuthority>()
                .AcquireDelegated(new(session.OwnerId, session.ProfileId, session.ProfileRevision, session.Id), ct);
            var current = journal.CustomerSession(session.Id, session.OwnerId);
            guard.ValidateCurrent();
            return Task.FromResult(!guard.IsProfileRevoked(session.ProfileId) && conversations.IsRealtimeTurnCurrent(call)
                && (memoryAccess is null || memory.StoreEpochFor(memoryAccess) == factEpoch)
                && current is { Snapshot.Length: > 0, EndRequested: false }
                && current.ProfileId == captured.Profile.ProfileId && current.ProfileRevision == captured.Profile.Revision
                && current.State is not ("completed" or "failed" or "recovering"));
        }
        var result = await services.GetRequiredService<WandGenerationService>()
            .GenerateAsync(session.OwnerId, session.OperationId, Authority, token, retry: true,
                evidence: captured.PersonalMemoryConsent ? evidence : null);
        if (result.ReadyMask != 0 && result.Status is not ("ready" or "selected"))
            throw new WandGenerationException(result.ErrorCode ?? "wand_generation_unavailable", 503);
        if (!await Authority(token) || result.ProfileId != session.ProfileId || result.ProfileRevision != session.ProfileRevision)
            throw new WandGenerationException("wand_generation_stale");
        return call with { Context = call.Context with { GeneratedWands = result.SelectedWand is { } selected ? [selected] : result.Candidates } };
    }

    internal static string AvailableForPrompt(RealtimeTurnContext turn) => turn.DeferWandSelection
        ? "Šest digitálních hůlkových památek (1–6); osobní názvy a příběhy budou připraveny při výběru."
        : turn.GeneratedWands is null
            ? PartyWandCatalog.AvailableForPrompt(turn.Request.ReadyMask)
            : JsonSerializer.Serialize(turn.GeneratedWands.Where(w => (turn.Request.ReadyMask & (1 << (w.PhysicalSlot - 1))) != 0)
            .Select(w => new { compartment = w.PhysicalSlot, name = w.Name,
                temperament = GptLive.LiveTextBudget.Bound(w.Temperament, 96), affinity = GptLive.LiveTextBudget.Bound(w.Affinity, 96) }),
            RealtimeProtocol.JsonOptions);

    internal static string ActorInstructions(RealtimeTurnContext turn) => turn.GeneratedWands is null ? ""
        : turn.DeferWandSelection
        ? "\n" + KeepsakeInstructions
            + "Osobní kandidáti ještě nebyli vygenerováni; to neznamená nedostupnost památek. "
            + "Pokračuj v rozhovoru, nevymýšlej názvy ani potvrzený výběr. Přípravu a výběr spustí samostatná akce testera."
        : "\n" + KeepsakeInstructions
            + "Pro tuto postavu platí výhradně nové osobní kandidáty v citovaných datech níže. Staré katalogové názvy a jejich lore nepoužívej. "
            + "Výběr nadále dělá backendová delegace. V závěru vyslov pouze název potvrzeného kandidáta, bez čísla přihrádky. "
            + "Jde o digitální fikci; nemění vzhled ani schopnosti fyzické rekvizity. Netvrď dokončený pohyb. "
            + AvailableForPrompt(turn);
}
