using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;

namespace Kufr.Backend.Features.Wands;

internal static partial class MobileWandWorkshopEndpoints
{
    private sealed record AudioTranscription(string Text);
    private sealed record WorkshopAudioRequest(string? QuestionId, int? QuestionRevision, string? WandId);
    private sealed record WorkshopEligibility(bool Eligible, string? Reason, object? Profile, int Tier,
        MobileWandWorkshopState? Session);

    internal static void MapMobileWandWorkshop(this RouteGroupBuilder group)
    {
        group.MapWorkshopConversation();
        group.MapGet("/workshop", async (int? upgrade, HttpContext http, CharacterProfileStore profiles,
            WandGenerationStore wands, GameStore games) =>
        {
            if (upgrade is not (null or 0 or 1)) throw new WandGenerationException("invalid_wand_workshop_tier", 400);
            var owner = Owner(http);
            var profile = await profiles.ReadAsync(owner, http.RequestAborted);
            if (profile is null)
                return Results.Ok(new WorkshopEligibility(false, "character_required", null, 0, null));

            var tier = Math.Max(MobileWandSessions.Current(wands, owner, profile.ProfileId, profile.Revision) is null ? 0 : 1,
                wands.MobileTier(owner, profile.ProfileId, profile.Revision));
            var workshopTier = upgrade == 1 ? 2 : 1;
            var operation = wands.FindWorkshop(owner, profile.ProfileId, profile.Revision, workshopTier);
            var session = operation is null ? null : State(http, wands, operation, owner);
            var unavailable = await UnavailableReasonAsync(http, games, owner, isUpgrade: upgrade == 1);
            if (operation is not null && !await MatchesCurrentLaunchAsync(http, wands, owner, operation))
            {
                session = null;
                unavailable ??= "wand_session_unavailable";
            }
            var projectedProfile = new { profile.ProfileId, profile.Revision, profile.UserInput.Name,
                profile.UserInput.World, profile.UserInput.Version, profile.UserInput.Description,
                profile.Status };
            if (await profiles.CapturePreparedAsync(owner, profile.ProfileId, profile.Revision, http.RequestAborted) is null)
                unavailable = profile.Status == "needs_clarification" ? "wand_character_needs_clarification"
                    : profile.Status == "failed" ? "wand_character_preparation_failed" : "wand_character_preparing";
            if (operation is not null && unavailable is null) {
                wands.ResumeWorkshopAfterBoss(owner, operation);
                session = State(http, wands, operation, owner);
            } else if (operation is not null && unavailable == "wand_workshop_boss_captured") {
                wands.PauseWorkshopForBoss(owner, operation);
                session = State(http, wands, operation, owner);
            }
            var available = unavailable is null;
            return Results.Ok(new WorkshopEligibility(available, available ? null : unavailable, projectedProfile, tier, session));
        });

        group.MapGet("/workshop/sessions/current", async (int? upgrade, HttpContext http,
            CharacterProfileStore profiles, WandGenerationStore wands, GameStore games) =>
        {
            if (upgrade is not (null or 0 or 1)) throw new WandGenerationException("invalid_wand_workshop_tier", 400);
            var owner = Owner(http);
            var profile = await profiles.ReadAsync(owner, http.RequestAborted)
                ?? throw new WandGenerationException("character_required", 409);
            var tier = upgrade == 1 ? 2 : 1;
            var operation = wands.FindWorkshop(owner, profile.ProfileId, profile.Revision, tier);
            if (operation is null) return Results.Ok(new { session = (MobileWandWorkshopState?)null });
            await RequireAvailableAsync(http, games, owner, isUpgrade: tier == 2);
            await RequireWorkshopLaunchSnapshotAsync(http, wands, owner, operation);
            var state = State(http, wands, operation, owner);
            await RequireProfileRevisionAsync(profiles, owner, state, http.RequestAborted);
            return Results.Ok(new { session = state });
        });

        group.MapPost("/workshop/sessions", async (MobileWandWorkshopStart request, HttpContext http,
            CharacterProfileStore profiles, WandGenerationStore wands, GameStore games,
            AccountDatabase accounts) =>
        {
            if (!Guid.TryParseExact(request.OperationId, "N", out var parsed) || parsed == Guid.Empty)
                throw new WandGenerationException("invalid_wand_operation", 400);
            var owner = Owner(http);
            var profile = await profiles.ReadAsync(owner, http.RequestAborted)
                ?? throw new WandGenerationException("character_required", 409);
            var captured = await profiles.CapturePreparedAsync(owner, profile.ProfileId, profile.Revision, http.RequestAborted)
                ?? throw new WandGenerationException("wand_character_preparing", 409);
            var current = MobileWandSessions.Current(wands, owner, profile.ProfileId, profile.Revision);
            var tier = request.Upgrade ? 2 : 1;
            var existing = wands.FindWorkshop(owner, profile.ProfileId, profile.Revision, tier);
            await RequireAvailableAsync(http, games, owner, request.Upgrade);
            if (existing is not null) await RequireWorkshopLaunchSnapshotAsync(http, wands, owner, existing);
            var operationId = request.OperationId;
            if (existing is null)
            {
                if (request.Upgrade)
                {
                    var groupId = await GameBossEndpoints.ParticipantGroupAsync(http, requireWand: false);
                    if (current is null || games.Stats(groupId, owner, "").Xp < 30)
                        throw new WandGenerationException("wand_upgrade_locked", 409);
                    if (wands.MobileTier(owner, profile.ProfileId, profile.Revision) >= 2)
                        throw new WandGenerationException("wand_upgrade_completed", 409);
                }
                else if (current is not null) throw new WandGenerationException("wand_already_created", 409);

                var priorMobile = wands.MobileOperation(owner, profile.ProfileId, profile.Revision, tier);
                if (priorMobile is not null)
                {
                    var priorGeneration = wands.Read(owner, priorMobile);
                    if (priorGeneration is null || priorGeneration.Status == "queued" && priorGeneration.ReadyMask == 1)
                        operationId = priorMobile; // Recover a crash after the durable mobile reservation.
                    else
                        wands.RetireMobileOperation(owner, priorMobile); // Legacy pre-generated candidates never enter this flow.
                }
                var launchGroup = await GameBossEndpoints.ParticipantGroupAsync(http, requireWand: false);
                var membership = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner
                    && user.LaunchGroupId == launchGroup && user.LaunchGroupRevokedAt == null
                    && !user.Disabled && !accounts.DeletionRequests.Any(deletion => deletion.UserId == owner),
                    http.RequestAborted) ?? throw new WandGenerationException("game_group_inactive", 403);
                wands.BeginMobile(owner, operationId, captured, tier);
                wands.BeginWorkshop(owner, operationId, captured, tier,
                    launchGroup, membership.LaunchGroupAccessRevision);
            }
            else if (existing != operationId && wands.MobileOperationTier(owner, operationId) is not (1 or 2))
            {
                // One resumable operation per owner, profile revision and tier.
                return Results.Ok(new { session = State(http, wands, existing, owner) });
            }
            return Results.Ok(new { session = State(http, wands, existing ?? operationId, owner) });
        });

        group.MapGet("/workshop/sessions/{id}", async (string id, HttpContext http,
            CharacterProfileStore profiles, WandGenerationStore wands, GameStore games) =>
        {
            var state = State(http, wands, id, Owner(http));
            await RequireAvailableAsync(http, games, Owner(http), state.Tier == 2, allowCompleted: state.Status == "complete");
            await RequireProfileRevisionAsync(profiles, Owner(http), state, http.RequestAborted);
            await RequireWorkshopLaunchSnapshotAsync(http, wands, Owner(http), id);
            return Results.Ok(new { session = state });
        });

        group.MapPost("/workshop/sessions/{id}/retry", async (string id, HttpContext http,
            CharacterProfileStore profiles, WandGenerationStore wands, GameStore games) =>
        {
            var owner = Owner(http);
            var state = State(http, wands, id, owner);
            await RequireAvailableAsync(http, games, owner, state.Tier == 2);
            await RequireProfileRevisionAsync(profiles, owner, state, http.RequestAborted);
            await RequireWorkshopLaunchSnapshotAsync(http, wands, owner, id);
            if (state.Status != "failed" || !state.CanRetry)
                throw new WandGenerationException("wand_generation_retry_unavailable");
            if (state.RecoveryKind != "generation")
                throw new WandGenerationException("wand_generation_retry_unavailable");
            wands.ResumeWorkshop(owner, id);
            var snapshot = await profiles.CapturePreparedAsync(owner, state.ProfileId, state.ProfileRevision, http.RequestAborted)
                ?? throw new WandGenerationException("wand_character_preparing", 409);
            _ = wands.Begin(owner, id, snapshot, 1, openAiConsent: true, outputLanguage: "cs");
            return Results.Ok(new { session = State(http, wands, id, owner) });
        });

        group.MapPost("/workshop/sessions/{id}/artwork/retry", async (string id, HttpContext http,
            WandGenerationStore wands, GameStore games) =>
        {
            var owner = Owner(http);
            var state = State(http, wands, id, owner);
            await RequireAvailableAsync(http, games, owner, state.Tier == 2,
                allowCompleted: state.Status == "complete");
            await RequireWorkshopLaunchSnapshotAsync(http, wands, owner, id);
            var selected = wands.Read(owner, id)?.SelectedWand
                ?? throw new WandGenerationException("wand_result_unavailable");
            var keepsakes = http.RequestServices.GetService<KeepsakeStore>();
            if (keepsakes is null) throw new WandGenerationException("wand_artwork_unavailable", 503);
            if (!MobileWandWorkshopWorker.RealArtworkConfigured(http.RequestServices.GetRequiredService<IConfiguration>()))
            {
                wands.RecordWorkshopArtworkUnavailable(owner, id, "wand_artwork_provider_unconfigured");
                return Results.Ok(new { session = State(http, wands, id, owner) });
            }
            var key = http.Request.Headers["Idempotency-Key"].ToString();
            if (!Guid.TryParseExact(key, "D", out _)) throw new WandGenerationException("invalid_wand_operation", 400);
            string locator;
            if (state.KeepsakeId is { Length: > 0 } existing)
            {
                locator = existing;
            }
            else
            {
                var sessionId = http.User.FindFirstValue(AccountIdentity.SessionClaim)
                    ?? throw new WandGenerationException("account_required", 401);
                locator = keepsakes.Issue(MobileWandWorkshopWorker.ArtworkSnapshot(selected), new(owner, sessionId)).Id;
                wands.AttachWorkshopKeepsake(owner, id, locator);
            }

            var artwork = keepsakes.FindOwnedWandArtwork(owner, selected.WandId);
            if (artwork is { CanRetry: true } && artwork.Status is "failed" or "missing")
                keepsakes.RetryWandArtwork(locator, AccountOwnedArchives.Actor(http), key);
            return Results.Ok(new { session = State(http, wands, id, owner) });
        });

        group.MapPost("/workshop/sessions/{id}/audio", async (string id, WorkshopAudioRequest request, HttpContext http,
            WandGenerationStore wands, GameStore games, CharacterProfileStore profiles,
            Kufr.Backend.Features.Show.ShowSpeechProvider speech) =>
        {
            var owner = Owner(http);
            var state = State(http, wands, id, owner);
            await RequireAvailableAsync(http, games, owner, state.Tier == 2);
            await RequireProfileRevisionAsync(profiles, owner, state, http.RequestAborted);
            if (state.Status != "complete" || request.WandId != state.Wand?.WandId)
                throw new WandGenerationException("wand_audio_revision_conflict", 409);
            var text = state.VerdictText();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var wav = await speech.SpeakAsync(text,
                Kufr.Backend.Features.Speech.SpeechOptions.Default, timeout.Token);
            var current = State(http, wands, id, owner);
            if (!await MatchesCurrentLaunchAsync(http, wands, owner, id) || current.Wand?.WandId != request.WandId)
                throw new WandGenerationException("wand_audio_revision_conflict", 409);
            await RequireAvailableAsync(http, games, owner, state.Tier == 2);
            return Results.File(wav, "audio/wav");
        });

    }

    private static async Task RequireProfileRevisionAsync(CharacterProfileStore profiles, string owner,
        MobileWandWorkshopState state, CancellationToken token)
    {
        if (await profiles.CapturePreparedAsync(owner, state.ProfileId, state.ProfileRevision, token) is null)
            throw new WandGenerationException("wand_character_preparing", 409);
    }

    private static async Task RequireWorkshopLaunchSnapshotAsync(HttpContext http,
        WandGenerationStore wands, string owner, string operationId)
    {
        if (!await MatchesCurrentLaunchAsync(http, wands, owner, operationId))
            throw new WandGenerationException("wand_session_unavailable", 404);
    }

    private static async Task<bool> MatchesCurrentLaunchAsync(HttpContext http,
        WandGenerationStore wands, string owner, string operationId)
    {
        string groupId;
        try { groupId = await GameBossEndpoints.ParticipantGroupAsync(http, requireWand: false); }
        catch (GameFault) { return false; }
        var accounts = http.RequestServices.GetRequiredService<AccountDatabase>();
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner
            && item.LaunchGroupId == groupId && item.LaunchGroupRevokedAt == null && !item.Disabled
            && !accounts.DeletionRequests.Any(deletion => deletion.UserId == owner), http.RequestAborted);
        if (user is null) return false;
        var roles = await http.RequestServices.GetRequiredService<UserManager<AccountUser>>().GetRolesAsync(user);
        if (!roles.Contains(AccountRoles.Customer, StringComparer.Ordinal)) return false;
        if (!(await http.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(http.User)).Allowed)
            return false;
        return wands.WorkshopMatchesLaunchSnapshot(owner, operationId, groupId, user.LaunchGroupAccessRevision);
    }

    private static async Task<string?> UnavailableReasonAsync(HttpContext http, GameStore games,
        string owner, bool isUpgrade)
    {
        try
        {
            var group = await GameBossEndpoints.ParticipantGroupAsync(http, requireWand: false);
            var boss = games.ReadBoss(group, owner);
            if (boss.Revealed && !boss.Rescued) return "wand_workshop_boss_captured";
            if (isUpgrade && games.Stats(group, owner, "").Xp < 30) return "wand_upgrade_locked";
            return null;
        }
        catch (GameFault fault) { return fault.Code; }
    }

    private static async Task RequireAvailableAsync(HttpContext http, GameStore games, string owner,
        bool isUpgrade, bool allowCompleted = false)
    {
        if (allowCompleted) return;
        var reason = await UnavailableReasonAsync(http, games, owner, isUpgrade);
        if (reason is not null) throw new WandGenerationException(reason, 409);
    }

    private static MobileWandWorkshopState State(HttpContext http, WandGenerationStore wands,
        string operationId, string owner)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)
            || wands.MobileOperationTier(owner, operationId) is not (1 or 2))
            throw new WandGenerationException("wand_session_unavailable", 404);
        var state = wands.ReadWorkshopState(owner, operationId);
        var artwork = state.KeepsakeId is { Length: > 0 }
            ? http.RequestServices.GetService<KeepsakeStore>()?.FindOwnedWandArtwork(owner, state.Wand?.WandId ?? "")
            : state.Artwork ?? (state.Wand is null ? null : new OwnedWandArtworkReference("", null, "queued"));
        return state with { Artwork = artwork };
    }

    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new WandGenerationException("account_required", 401);

    private static string StepEvidence(MobileWandWorkshopStep step) => JsonSerializer.Serialize(new
    {
        step.QuestionId, step.Question, step.AnswerId, step.AnswerRevision,
        answer = step.Answer, step.Skipped, step.SourceMode
    });

    private static string StableFactId(string profileId, long revision, string text, string[] sources) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(profileId + "\n" + revision + "\n" + text
                + "\n" + string.Join("\n", sources.Order(StringComparer.Ordinal))))).ToLowerInvariant();
}

internal static class MobileWandWorkshopPresentation
{
    internal static string VerdictText(this MobileWandWorkshopState state)
    {
        if (state.Wand is not { } wand) throw new WandGenerationException("wand_result_unavailable", 409);
        var callback = state.Messages?.LastOrDefault(message => message.Role == "user" && message.Text != "Zahaj rozhovor s připravenou postavou.")?.Text
            ?? state.Steps.LastOrDefault(step => step.Answer is not null)?.Answer;
        var detail = callback is null ? "Dílna si poznamenala i tvé velkorysé mlčení."
            : $"A k tomu tvému „{Speech(callback, 120)}“ si hůlka zjevně udělala vlastní poznámku.";
        return $"Hotovo. {Speech(wand.Name, 100)}, {Speech(wand.Material, 160)} a jádro z {Speech(wand.Core, 160)}. {detail}";
    }

    private static string Speech(string value, int maximum)
    {
        var safe = new string(value.Where(character => !char.IsControl(character) && character != '?').ToArray()).Trim();
        return safe.Length <= maximum ? safe : safe[..maximum].TrimEnd() + "…";
    }
}
