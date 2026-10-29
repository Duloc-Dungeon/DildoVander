using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Wands;

internal sealed record WorkshopMessageRequest(string MessageId, string Text);
internal sealed record WorkshopMessageCorrection(int ExpectedRevision, string Text);
internal sealed record WorkshopRepairRequest(int ExpectedVersion, string SourceAssetId, string SourceSha256);
internal sealed record WorkshopMomentRequest(string MessageId, int ExpectedRevision);

internal static partial class MobileWandWorkshopEndpoints
{
    private static void MapWorkshopConversation(this RouteGroupBuilder group)
    {
        group.MapPost("/workshop/sessions/{id}/moments", async (string id, WorkshopMomentRequest request,
            HttpContext http, WandGenerationStore wands, CharacterProfileStore profiles, GameStore games,
            [FromServices] Kufr.Backend.Features.Chronicle.ChronicleStore chronicle) =>
        {
            var owner = Owner(http);
            var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games, allowFinished: true, allowHistoricalComplete: true);
            if (request.MessageId is not { Length: > 0 and <= 120 } || request.ExpectedRevision < 1)
            { throw new WandGenerationException("invalid_workshop_message", 400); }
            var sourceId = wands.SaveWorkshopMoment(owner, id, request.MessageId, request.ExpectedRevision);
            wands.DrainWandChronicle(chronicle);
            return Results.Ok(new { sourceId });
        });
        group.MapPost("/workshop/sessions/{id}/messages", async (string id, WorkshopMessageRequest request,
            HttpContext http, WandGenerationStore wands, CharacterProfileStore profiles, GameStore games,
            [FromServices] MobileWorkshopLiveRuntime runtime, [FromServices] IMobileWandQuestionProvider dialogue) =>
        {
            var owner = Owner(http);
            var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games);
            if (!Guid.TryParseExact(request.MessageId, "N", out _) || request.Text is null || request.Text.Length > 2000)
                throw new WandGenerationException("invalid_workshop_message", 400);
            if (!runtime.TryEnter(owner, id)) throw new WandGenerationException("wand_session_busy", 409);
            try
            {
                // An uncertain previous provider call is never blindly repeated by an HTTP retry.
                var messages = wands.ReadWorkshopMessages(owner, id);
                var text = string.IsNullOrWhiteSpace(request.Text)
                    ? "Zahaj rozhovor s připravenou postavou." : request.Text.Trim();
                var previous = messages.FirstOrDefault(message => message.MessageId == request.MessageId);
                if (previous is not null && (previous.Role != "user" || previous.Text != text || previous.SourceMode != "text"))
                {
                    throw new WandGenerationException("wand_message_id_conflict", 409);
                }
                if (messages.Any(message => message.MessageId == request.MessageId || message.MessageId == MobileWorkshopLiveRuntime.MessageId("reply-" + request.MessageId)))
                    return Results.Ok(new { session = State(http, wands, id, owner) });
                wands.AppendWorkshopMessage(owner, id, request.MessageId, "user",
                    text, "text");
                var observed = wands.ReadWorkshopMessages(owner, id);
                var reply = await dialogue.ReplyAsync(wands.ReadWorkshopProfile(owner, id), observed, http.RequestAborted);
                try { await RequireConversationAsync(http, state, profiles, wands, games); }
                catch (WandGenerationException error) when (error.Code == "wand_workshop_boss_captured") {
                    wands.PauseWorkshopForBoss(owner, id);
                    wands.AppendWorkshopMessage(owner, id, MobileWorkshopLiveRuntime.MessageId("reply-" + request.MessageId), "assistant", reply.Reply, "text");
                    if (reply.ReadyToFinish && !string.IsNullOrWhiteSpace(request.Text)) wands.FinishWorkshopConversation(owner, id, state.ProfileId, state.ProfileRevision, WandGenerationStore.UserEvidenceDigest(observed));
                    return Results.Ok(new { session = State(http, wands, id, owner) });
                }
                if (!MobileWorkshopLiveRuntime.SameUserEvidence(observed, wands.ReadWorkshopMessages(owner, id)))
                    return Results.Ok(new { session = State(http, wands, id, owner) });
                wands.AppendWorkshopMessage(owner, id, MobileWorkshopLiveRuntime.MessageId("reply-" + request.MessageId), "assistant", reply.Reply, "text");
                if (reply.ReadyToFinish && !string.IsNullOrWhiteSpace(request.Text))
                    wands.FinishWorkshopConversation(owner, id, state.ProfileId, state.ProfileRevision, WandGenerationStore.UserEvidenceDigest(observed));
                return Results.Ok(new { session = State(http, wands, id, owner) });
            }
            finally { runtime.Exit(owner, id); }
        });
        group.MapPut("/workshop/sessions/{id}/messages/{messageId}", async (string id, string messageId,
            WorkshopMessageCorrection request, HttpContext http, WandGenerationStore wands,
            CharacterProfileStore profiles, GameStore games) =>
        {
            var owner = Owner(http); var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games);
            wands.UpdateWorkshopMessage(owner, id, messageId, request.ExpectedRevision, request.Text, "text");
            return Results.Ok(new { session = State(http, wands, id, owner) });
        });
        group.MapPost("/workshop/sessions/{id}/finish", async (string id, HttpContext http,
            WandGenerationStore wands, CharacterProfileStore profiles, GameStore games, [FromServices] MobileWorkshopLiveRuntime runtime) =>
        {
            var owner = Owner(http); var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games, allowFinished: true);
            if (runtime.IsActive(owner, id)) throw new WandGenerationException("wand_stop_voice_before_finish", 409);
            wands.FinishWorkshopConversation(owner, id, state.ProfileId, state.ProfileRevision);
            return Results.Ok(new { session = State(http, wands, id, owner) });
        });
        group.MapPost("/workshop/sessions/{id}/ticket", async (string id, MobileWandTicketRequest request,
            HttpContext http, WandGenerationStore wands, CharacterProfileStore profiles, GameStore games,
            AccountDatabase accounts, MobileWandRuntime tickets) =>
        {
            var owner = Owner(http); var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games);
            var config = http.RequestServices.GetRequiredService<AccountConfiguration>();
            var origin = http.Request.Headers.Origin.ToString();
            if (!http.Request.IsHttps || !config.AllowedOrigins.Contains(origin, StringComparer.Ordinal)
                || !AllowedWorkshopSocketOrigin(request.SocketOrigin, config))
                throw new WandGenerationException("account_origin_required", 403);
            var user = await accounts.Users.AsNoTracking().SingleAsync(user => user.Id == owner, http.RequestAborted);
            var admission = new MobileWandAdmission(owner, id, http.User.FindFirstValue(AccountIdentity.SessionClaim)!,
                user.SecurityStamp ?? "", request.SocketOrigin, DateTimeOffset.UtcNow.AddSeconds(30), wands.ReadWorkshopProfile(owner, id));
            var ticket = tickets.Ticket(admission);
            var path = $"/app/v1/customer/wand/workshop/sessions/{id}/live";
            var url = new UriBuilder(origin) { Scheme = "wss", Path = path, Query = "ticket=" + ticket }.Uri.AbsoluteUri;
            return Results.Ok(new { url, path, expiresAt = admission.ExpiresAt });
        }).WithMetadata(new AccountIdentity.SocketTicketRateLimitMetadata());
        group.MapPost("/workshop/sessions/{id}/artwork/repair", async (string id, WorkshopRepairRequest request,
            HttpContext http, WandGenerationStore wands, CharacterProfileStore profiles, GameStore games, [FromServices] KeepsakeStore keepsakes) =>
        {
            var owner = Owner(http); var state = State(http, wands, id, owner);
            await RequireConversationAsync(http, state, profiles, wands, games, allowFinished: true);
            if (state.Wand is null || state.KeepsakeId is null) throw new WandGenerationException("wand_result_unavailable", 409);
            var repairKey = http.Request.Headers["Idempotency-Key"].ToString();
            if (!Guid.TryParseExact(repairKey, "D", out _)) throw new WandGenerationException("invalid_wand_operation", 400);
            keepsakes.RepairWandArtwork(state.KeepsakeId, AccountOwnedArchives.Actor(http),
                state.Wand.WandId, request.ExpectedVersion, request.SourceAssetId, request.SourceSha256,
                repairKey);
            return Results.Ok(new { session = State(http, wands, id, owner) });
        });
    }

    private static async Task RequireConversationAsync(HttpContext http, MobileWandWorkshopState state,
        CharacterProfileStore profiles, WandGenerationStore wands, GameStore games, bool allowFinished = false, bool allowHistoricalComplete = false)
    {
        var owner = Owner(http);
        await RequireAvailableAsync(http, games, owner, state.Tier == 2, allowCompleted: allowFinished && state.Status == "complete", operationId: state.Id);
        await RequireProfileRevisionAsync(profiles, wands, owner, state, http.RequestAborted, allowCompleted: allowHistoricalComplete);
        await RequireWorkshopLaunchSnapshotAsync(http, wands, owner, state.Id);
        if (!allowFinished && state.Status != "asking") throw new WandGenerationException("wand_conversation_closed", 409);
    }

    internal static bool AllowedWorkshopSocketOrigin(string origin, AccountConfiguration config) =>
        origin is "capacitor://localhost" or "https://localhost" or "http://localhost"
        || config.AllowedOrigins.Contains(origin, StringComparer.Ordinal);

    internal static void MapWorkshopLive(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) return;
        app.MapGet("/app/v1/customer/wand/workshop/sessions/{id}/live", async (string id, HttpContext http,
            MobileWandRuntime tickets, [FromServices] MobileWorkshopLiveRuntime runtime, AccountConfiguration config) =>
        {
            try
            {
                var origin = http.Request.Headers.Origin.ToString();
                if (!http.Request.IsHttps || !http.WebSockets.IsWebSocketRequest
                    || !AllowedWorkshopSocketOrigin(origin, config))
                    throw new WandGenerationException("account_origin_required", 403);
                var admission = tickets.Take(http.Request.Query["ticket"].ToString(), id, origin);
                await runtime.RequireAuthorityAsync(admission, http.RequestAborted);
                using var socket = await http.WebSockets.AcceptWebSocketAsync();
                await runtime.RunAsync(admission, socket, http.RequestAborted);
            }
            catch (WandGenerationException error) when (!http.Response.HasStarted)
            { await ApiProblem.Create(http, error.Status, error.Code).ExecuteAsync(http); }
        }).AllowAnonymous().RequireRateLimiting("account");
    }
}
