using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Observability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Customers;

internal sealed record PrelaunchCharacterWrite(long ExpectedRevision, string Name, string Universe,
    string? ExpectedProfileId = null, bool OpenAiConsent = false);
internal sealed record LoadingPreviewRequest(string Name, string Universe, string Prompt);
internal sealed record PortraitRetryRequest(string? ExpectedProfileId, long? ExpectedRevision);

internal static class PrelaunchCharacterEndpoints
{
    internal static void MapPrelaunchCharacters(this WebApplication app)
    {
        var group = app.MapGroup("/app/v1/prelaunch/character").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(RequireAccountAsync);
        group.MapGet("", async (HttpContext http, CharacterProfileStore store) =>
            Results.Ok(await ViewAsync(http, store)));
        group.MapPut("", async (PrelaunchCharacterWrite request, HttpContext http, CharacterProfileStore store) =>
        {
            if (await AccountAiConsent.RequireAsync(http) is { } consent) { return consent; }
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Universe)
                || request.Name.Any(char.IsControl) || request.Universe.Any(char.IsControl))
            {
                return ApiProblem.Create(http, 400, "invalid_character_input");
            }
            var current = await store.ReadAsync(Owner(http), http.RequestAborted);
            var input = new CharacterUserInput(request.Name.Trim(), request.Universe.Trim());
            if (current is not null && current.UserInput.Name.Trim() == input.Name && current.UserInput.World.Trim() == input.World)
            {
                // Opting an existing character into portraits must not discard its previously supplied details.
                input = current.UserInput;
            }
            await store.SaveAsync(Owner(http), new(request.ExpectedRevision,
                input, request.ExpectedProfileId,
                OpenAiConsent: true, PortraitConsent: true), http.RequestAborted, canEditExisting: AccountRoles.IsAdmin(http.User));
            return Results.Ok(await ViewAsync(http, store));
        });
        group.MapGet("/portrait", (string profileId, long revision, HttpContext http, CharacterProfileStore store) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var image = store.ReadPortraitImage(Owner(http), profileId, revision);
            return image is null ? ApiProblem.Create(http, 404, "portrait_unavailable") : Results.File(image, "image/webp");
        });
        group.MapPost("/retry", async (HttpContext http, CharacterProfileStore store) =>
        {
            if (await AccountAiConsent.RequireAsync(http) is { } consent) { return consent; }
            var expected = http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                ? await http.Request.ReadFromJsonAsync<PortraitRetryRequest>(http.RequestAborted) : null;
            if (expected is { ExpectedProfileId: null, ExpectedRevision: null }) { expected = null; }
            await RetryAsync(Owner(http), store, http.RequestAborted, expected);
            return Results.Ok(await ViewAsync(http, store));
        });

        var admin = app.MapGroup("/app/v1/admin/launch").RequireAuthorization().RequireRateLimiting("account");
        admin.AddEndpointFilter(RequireAccountAsync);
        admin.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (!AccountRoles.IsAdmin(http.User)) { return ApiProblem.Create(http, 403, "account_admin_required"); }
            if (http.Request.Method is not ("GET" or "HEAD"))
            {
                var recent = await AccountAuthorization.RequireRecentAsync(http);
                if (recent is not null) { return recent; }
                if (http.Request.Headers["X-DV-Confirm"] != "yes") { return ApiProblem.Create(http, 409, "confirmation_required"); }
            }
            return await next(invocation);
        });
        admin.MapGet("/artwork", (CharacterProfileStore store) => Results.Ok(new { jobs = store.PortraitJobSummaries() }));
        admin.MapPost("/artwork/{profileId}/retry", async (string profileId, HttpContext http, CharacterProfileStore store,
            AccountDatabase accounts) =>
        {
            var owner = store.PortraitOwner(profileId);
            if (owner is null) { return ApiProblem.Create(http, 404, "portrait_unavailable"); }
            var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && !user.Disabled,
                http.RequestAborted);
            if (user is null || !AccountAiConsent.HasConsent(user))
            {
                return ApiProblem.Create(http, 409, "ai_consent_required");
            }
            await RetryAsync(owner, store, http.RequestAborted);
            return Results.Ok(new { jobs = store.PortraitJobSummaries() });
        });
        admin.MapPost("/personalization-preview", async (LoadingPreviewRequest request, HttpContext http,
            ICharacterPortraitProvider provider) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100
                || string.IsNullOrWhiteSpace(request.Universe) || request.Universe.Length > 200
                || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4000)
            {
                return ApiProblem.Create(http, 400, "invalid_character_input");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var message = await provider.GenerateMessageAsync(new(request.Name, request.Universe), null,
                    PortraitContentPolicy.LoadingDirection(request.Prompt), deadline.Token);
                return !PortraitContentPolicy.ValidLoadingMessage(message)
                    ? ApiProblem.Create(http, 422, "loading_message_unavailable") : Results.Ok(new { message });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return ApiProblem.Create(http, 503, "loading_message_unavailable");
            }
        });
    }

    private static async ValueTask<object?> RequireAccountAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
            || !await http.RequestServices.GetRequiredService<AccountDatabase>().Users.AsNoTracking()
                .AnyAsync(user => user.Id == owner && !user.Disabled, http.RequestAborted))
        {
            return ApiProblem.Create(http, 401, "account_required");
        }
        try
        {
            using var admission = http.RequestServices.GetRequiredService<CustomerAdmissionAuthority>()
                .AcquireAccountCookie(http, http.RequestAborted);
            var write = invocation.Arguments.OfType<PrelaunchCharacterWrite>().FirstOrDefault();
            if (admission.Proof.FixedUniverse is { } universe && write is not null
                && !string.Equals(write.Universe?.Trim(), universe, StringComparison.Ordinal))
            {
                return ApiProblem.Create(http, 409, "invitation_universe_required");
            }
            if (admission.Proof.FixedUniverse is { } fixedUniverse && http.Request.Method == "POST"
                && http.Request.Path.Equals("/app/v1/prelaunch/character/retry", StringComparison.OrdinalIgnoreCase))
            {
                var current = await http.RequestServices.GetRequiredService<CharacterProfileStore>()
                    .ReadAsync(owner, http.RequestAborted);
                if (current is not null && current.UserInput.World.Trim() != fixedUniverse)
                {
                    return ApiProblem.Create(http, 409, "invitation_universe_required");
                }
            }
            return await next(invocation);
        }
        catch (CharacterException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        catch (CustomerDataAccessException error) { return ApiProblem.Create(http, error.Status, error.Code); }
    }

    private static async Task<object> ViewAsync(HttpContext http, CharacterProfileStore store)
    {
        var owner = Owner(http);
        var profile = await store.ReadAsync(owner, http.RequestAborted);
        var portrait = profile is null ? null : store.ReadPortrait(owner, profile.ProfileId, profile.Revision);
        // Return only reveal content and image attribution, never research or provider payloads.
        return new
        {
            character = profile is null ? null : new { profile.ProfileId, profile.Revision,
                name = profile.UserInput.Name, universe = profile.UserInput.World, profile.Status },
            artwork = portrait is null ? null : new { portrait.Status,
                loadingMessage = PortraitContentPolicy.ValidLoadingMessage(portrait.LoadingMessage) ? portrait.LoadingMessage : null,
                portrait.ErrorCode,
                finalMessage = portrait.FinalMessage ?? (portrait.Status == "ready"
                    ? CharacterWelcomePolicy.LegacyText(CharacterWelcomePolicy.Fallback) : null),
                welcome = portrait.Welcome is { } welcome
                    ? new { joke = welcome.Joke, identity = welcome.Identity, teaser = welcome.Teaser }
                    : null,
                source = portrait.Status == "ready" ? portrait.Source : null,
                url = portrait.Status == "ready"
                    ? $"/app/v1/prelaunch/character/portrait?profileId={Uri.EscapeDataString(profile!.ProfileId)}&revision={profile.Revision}" : null }
        };
    }

    private static async Task RetryAsync(string owner, CharacterProfileStore store, CancellationToken token,
        PortraitRetryRequest? expected = null)
    {
        var profile = await store.ReadAsync(owner, token) ?? throw new CharacterException("character_not_found", 404);
        if (expected is not null && (expected.ExpectedProfileId != profile.ProfileId || expected.ExpectedRevision != profile.Revision))
        {
            throw new CharacterException("character_revision_conflict", 409);
        }
        if (profile.Status == "saved")
        {
            if (!await store.RequestEnrichmentAsync(owner, token, profile.ProfileId, profile.Revision))
            {
                throw new CharacterException("portrait_retry_unavailable", 409);
            }
        }
        var portrait = store.ReadPortrait(owner, profile.ProfileId, profile.Revision);
        if (portrait is null)
        {
            if (!store.RequestPortrait(owner, profile.ProfileId, profile.Revision))
            {
                throw new CharacterException("portrait_retry_unavailable", 409);
            }
        }
        else if (portrait.Status == "failed" && !store.RetryPortrait(owner, profile.ProfileId, profile.Revision))
        {
            throw new CharacterException("portrait_retry_unavailable", 409);
        }
    }

    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}

internal sealed class CharacterPortraitWorker(IServiceScopeFactory scopes, CharacterProfileStore store,
    ICharacterPortraitProvider provider, ICharacterPortraitFallbackProvider fallback,
    ILogger<CharacterPortraitWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = store.ClaimPortrait();
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning("Character portrait worker storage failure {Reason}", CharacterProviderFailure.Code(error));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task ProcessAsync(CharacterPortraitJob job, CancellationToken stoppingToken)
    {
        using var activity = BackendTelemetry.StartOperation("character.portrait.process");
        activity?.SetTag("app.operation_id", job.ProfileId);
        activity?.SetTag("app.attempt", job.Attempt);
        using var scope = scopes.CreateScope();
        var authority = scope.ServiceProvider.GetRequiredService<CustomerAdmissionAuthority>();
        Task<bool> IsCurrentAsync()
        {
            try
            {
                using var admission = authority.AcquireAccount(job.AccountId, stoppingToken);
                return Task.FromResult(store.CanDispatchPortrait(job));
            }
            catch (CustomerDataAccessException) { return Task.FromResult(false); }
        }

        if (!await IsCurrentAsync())
        {
            store.CompletePortrait(job, null, "portrait_account_unavailable");
            return;
        }
        CharacterWorkLease work;
        try
        {
            using var admission = authority.AcquireAccount(job.AccountId, stoppingToken);
            work = store.BeginAccountWork(job.AccountId, stoppingToken);
        }
        catch (CustomerDataAccessException) { return; }
        using var activeWork = work;
        var config = scope.ServiceProvider.GetService<LaunchPolicyStore>()?.Read() ?? LaunchConfiguration.Default;
        var previous = store.ReadPortrait(job.AccountId, job.ProfileId, job.Revision);
        var welcome = CharacterWelcomePolicy.Valid(previous?.Welcome, job) ? previous!.Welcome! : null;
        var finalMessage = welcome is null ? CharacterWelcomePolicy.LegacyText(CharacterWelcomePolicy.Fallback)
            : CharacterWelcomePolicy.LegacyText(welcome);
        if (welcome is null && await IsCurrentAsync())
        {
            using var experienceDeadline = CancellationTokenSource.CreateLinkedTokenSource(activeWork.Token);
            experienceDeadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var experience = await provider.GenerateExperienceAsync(job,
                    config.PersonalizedLoadingPrompt, config.PersonalizedLoadingEnabled, experienceDeadline.Token);
                if (config.PersonalizedLoadingEnabled && PortraitContentPolicy.ValidLoadingMessage(experience.LoadingMessage)
                    && !store.SetPortraitMessage(job, experience.LoadingMessage!)) { return; }
                if (PortraitContentPolicy.ValidMessage(experience.FinalMessage)) { finalMessage = experience.FinalMessage; }
                if (CharacterWelcomePolicy.Valid(experience.Welcome, job)) { welcome = experience.Welcome!; }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Text is optional; a failed experience request never blocks the portrait.
                logger.LogInformation("Character portrait text unavailable {Reason}", CharacterProviderFailure.Code(error));
            }
        }
        if (!await IsCurrentAsync()) { return; }
        welcome ??= CharacterWelcomePolicy.Fallback;
        if (!store.SetPortraitExperience(job, finalMessage, welcome)) { return; }
        using var imageDeadline = CancellationTokenSource.CreateLinkedTokenSource(activeWork.Token);
        imageDeadline.CancelAfter(TimeSpan.FromSeconds(180));
        byte[]? image = null;
        CharacterPortraitSource? source = null;
        Exception? generationFailure = null;
        try
        {
            image = await provider.GeneratePortraitAsync(job, imageDeadline.Token);
            if (image is not { Length: > 0 and <= 8 * 1024 * 1024 })
            {
                throw new InvalidDataException("Portrait size invalid.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            image = null;
            generationFailure = error;
            var reason = CharacterProviderFailure.Code(error);
            if (activeWork.Token.IsCancellationRequested || stoppingToken.IsCancellationRequested || !await IsCurrentAsync())
            {
                activity?.SetTag("app.outcome", "discarded");
                return;
            }
            logger.LogWarning("Character portrait generation failed; searching for a source image ({Reason}).", reason);
            try
            {
                // Claim leases last 240s; text, image and fallback budgets total at
                // most 220s, leaving room for admission and the fenced write.
                using var fallbackDeadline = CancellationTokenSource.CreateLinkedTokenSource(activeWork.Token);
                fallbackDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                var found = await fallback.FindAsync(job, fallbackDeadline.Token);
                if (found is not null)
                {
                    image = found.Image;
                    source = found.Source;
                }
            }
            catch (Exception fallbackError) when (fallbackError is not OutOfMemoryException)
            {
                logger.LogWarning("Character portrait internet fallback unavailable ({Reason}).", CharacterProviderFailure.Code(fallbackError));
            }
        }
        if (activeWork.Token.IsCancellationRequested || stoppingToken.IsCancellationRequested || !await IsCurrentAsync())
        {
            activity?.SetTag("app.outcome", "discarded");
            return;
        }
        using var completionAdmission = authority.AcquireAccount(job.AccountId, stoppingToken);
        if (image is not null)
        {
            var completed = store.CompletePortrait(job, image, null, finalMessage, welcome, source);
            activity?.SetTag("app.outcome", completed ? "completed" : "discarded");
            logger.LogInformation("Character portrait {Outcome} {ProfileId} {Revision} {Attempt}; internet fallback {InternetFallback}",
                completed ? "completed" : "discarded", job.ProfileId, job.Revision, job.Attempt, source is not null);
            return;
        }
        var failure = generationFailure ?? new InvalidDataException("Portrait missing.");
        var failureCode = CharacterProviderFailure.Code(failure);
        BackendTelemetry.Failed(activity, failure, stoppingToken);
        store.CompletePortrait(job, null, failureCode);
        logger.LogWarning("Character portrait failed {ProfileId} {Revision} {Reason}", job.ProfileId, job.Revision, failureCode);
    }
}
