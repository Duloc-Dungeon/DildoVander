using System.Security.Claims;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.Game;

internal sealed record GameBossNarrationRequest(long ExpectedRevision);
internal sealed record GameBossNarrationView(long Revision, string Text, bool VoiceAvailable);

// Providers run outside gameplay transactions. A reply is immutable for one
// disclosed context and revision; reconnects reuse it instead of billing again.
internal sealed class GameBossNarration(GameStore store, IGameBossNarrator narrator, IGameBossSpeech speech,
    ILogger<GameBossNarration> logger)
{
    private readonly SemaphoreSlim slots = new(2, 2);

    internal async Task<GameBossNarrationView> NarrateAsync(string group, string user, string id, long revision,
        Func<CancellationToken, Task<GameBossNarrationInput>> readCurrent, CancellationToken token)
    {
        var input = await readCurrent(token);
        var key = input.Key;
        var cached = store.ReadBossNarration(group, user, id, revision, key);
        if (cached?.Text is { } existing) { return new(revision, existing, speech.Enabled); }
        if (!await slots.WaitAsync(0, token)) { throw new GameFault(503, "boss_narration_busy"); }
        string? claim = null;
        try
        {
            claim = store.ClaimBossNarration(group, user, id, revision, key)
                ?? throw new GameFault(503, "boss_narration_busy");
            // Another request may have published between our first read and the claim.
            cached = store.ReadBossNarration(group, user, id, revision, key);
            if (cached?.Text is { } ready) { return new(revision, ready, speech.Enabled); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var text = (await narrator.NarrateBossAsync(input, deadline.Token)).Trim();
            // Validate the words before caching. Delivery tags are added only for audio.
            _ = GameBossDelivery.For(input.Match).Speak(text);
            await RequireCurrentAsync(readCurrent, key, deadline.Token);
            store.PublishBossNarration(group, user, id, revision, key, claim, text, null);
            return new(revision, text, speech.Enabled);
        }
        catch (GameFault) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Baron text unavailable ({ErrorType}).", error.GetType().Name);
            throw new GameFault(503, "boss_narration_unavailable");
        }
        finally
        {
            if (claim is not null) { store.ReleaseBossNarration(id, claim); }
            slots.Release();
        }
    }

    internal async Task<byte[]> AudioAsync(string group, string user, string id, long revision,
        Func<CancellationToken, Task<GameBossNarrationInput>> readCurrent, CancellationToken token)
    {
        var input = await readCurrent(token);
        var rendering = speech.Capture();
        var key = input.Key;
        var cached = store.ReadBossNarration(group, user, id, revision, key);
        if (cached?.Audio is { } existing && cached.AudioKey == rendering.CacheKey) { return existing; }
        if (cached?.Text is null) { throw new GameFault(409, "boss_narration_not_ready"); }
        if (!rendering.Enabled) { throw new GameFault(503, "boss_voice_unavailable"); }
        if (!await slots.WaitAsync(0, token)) { throw new GameFault(503, "boss_narration_busy"); }
        string? claim = null;
        try
        {
            claim = store.ClaimBossNarration(group, user, id, revision, key)
                ?? throw new GameFault(503, "boss_narration_busy");
            cached = store.ReadBossNarration(group, user, id, revision, key);
            if (cached?.Audio is { } ready && cached.AudioKey == rendering.CacheKey) { return ready; }
            if (cached?.Text is null) { throw new GameFault(409, "boss_narration_not_ready"); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var audio = await rendering.SpeakAsync(GameBossDelivery.For(input.Match).Speak(cached.Text), deadline.Token);
            if (audio.Length is < 1 or > ElevenLabsBossSpeech.MaxAudioBytes)
            { throw new InvalidDataException("Invalid Baron audio."); }
            await RequireCurrentAsync(readCurrent, key, deadline.Token);
            store.PublishBossNarration(group, user, id, revision, key, claim, null, audio, rendering.CacheKey);
            return audio;
        }
        catch (GameFault) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Baron audio unavailable ({ErrorType}).", error.GetType().Name);
            throw new GameFault(503, "boss_voice_unavailable");
        }
        finally
        {
            if (claim is not null) { store.ReleaseBossNarration(id, claim); }
            slots.Release();
        }
    }

    private static async Task RequireCurrentAsync(Func<CancellationToken, Task<GameBossNarrationInput>> readCurrent,
        string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((await readCurrent(token)).Key != key) { throw new GameFault(409, "boss_narration_revision_conflict"); }
    }
}

internal static class GameBossNarrationEndpoints
{
    internal static async Task<IResult> NarrateAsync(string id, GameBossNarrationRequest request, HttpContext http,
        GameBossNarration narration)
    {
        var group = (string)http.Items["boss-group"]!;
        var user = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var reply = await narration.NarrateAsync(group, user, id, request.ExpectedRevision,
            token => ReadInputAsync(http, group, user, id, token), http.RequestAborted);
        return Results.Ok(reply);
    }

    internal static async Task<IResult> AudioAsync(string id, GameBossNarrationRequest request, HttpContext http,
        GameBossNarration narration)
    {
        var group = (string)http.Items["boss-group"]!;
        var user = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var audio = await narration.AudioAsync(group, user, id, request.ExpectedRevision,
            token => ReadInputAsync(http, group, user, id, token), http.RequestAborted);
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(audio, "audio/mpeg", enableRangeProcessing: false);
    }

    private static async Task<GameBossNarrationInput> ReadInputAsync(HttpContext http, string group, string user,
        string id, CancellationToken token)
    {
        var accounts = http.RequestServices.GetRequiredService<AccountDatabase>();
        var session = http.User.FindFirstValue(AccountIdentity.SessionClaim);
        var current = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(account => account.Id == user
            && !account.Disabled && !accounts.DeletionRequests.Any(request => request.UserId == account.Id), token);
        var stampClaim = http.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        if (current is null || session is null || http.User.FindFirstValue(ClaimTypes.NameIdentifier) != user
            || current.SecurityStamp != http.User.FindFirstValue(stampClaim)
            || !await accounts.Sessions.AsNoTracking().AnyAsync(accountSession => accountSession.Id == session
                && accountSession.UserId == user && accountSession.ExpiresAt > AccountEndpoints.Now(), token))
        { throw new GameFault(401, "account_required"); }
        if (!AccountAiConsent.HasConsent(current)) { throw new GameFault(409, "ai_consent_required"); }
        // Recheck membership/launch after provider I/O, including revocation during generation.
        if (await GameBossEndpoints.ParticipantGroupAsync(http, requireWand: false) != group)
        { throw new GameFault(403, "game_group_required"); }
        var store = http.RequestServices.GetRequiredService<GameStore>();
        var match = store.ReadBossAttempt(group, user, id);
        var player = await GameYoloWorker.ReadPublicProfile(user, "Soupeř",
            http.RequestServices.GetRequiredService<CharacterProfileStore>(),
            http.RequestServices.GetRequiredService<WandGenerationStore>(),
            accounts, token);
        return new(match, player, store.ReadBoss(group).Rescued);
    }
}
