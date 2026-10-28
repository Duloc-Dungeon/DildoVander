using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Kufr.Backend.Features.Observability;

namespace Kufr.Backend.Features.Customers;

internal static class CharacterEndpoints
{
    internal static IServiceCollection AddCustomerCharacters(this IServiceCollection services, IConfiguration configuration)
    {
        if (!AccountConfiguration.Read(configuration).Enabled) { return services; }
        services.AddSingleton(provider => new CharacterProfileStore(provider.GetRequiredService<AccountConfiguration>().DataRoot));
        services.AddSingleton<CustomerAdmissionAuthority>();
        services.TryAddSingleton<ICharacterEnrichmentProvider, OpenAiCharacterEnrichmentProvider>();
        services.AddHostedService<CharacterEnrichmentWorker>();
        services.TryAddSingleton<ICharacterPortraitProvider, OpenAiCharacterPortraitProvider>();
        services.AddHostedService<CharacterPortraitWorker>();
        return services;
    }

    internal static void MapCustomerCharacters(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        app.MapPrelaunchCharacters();
        var group = app.MapGroup("/app/v1/customer/character").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var accountId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (accountId is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
            {
                return ApiProblem.Create(http, 401, "account_required");
            }
            // Defense in depth for hosts that do not install cookie revalidation.
            var db = http.RequestServices.GetRequiredService<AccountDatabase>();
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == accountId && !user.Disabled, http.RequestAborted))
            {
                return ApiProblem.Create(http, 401, "account_required");
            }
            try
            {
                using var admission = http.RequestServices.GetRequiredService<CustomerAdmissionAuthority>()
                    .AcquireAccountCookie(http, http.RequestAborted);
                var write = invocation.Arguments.OfType<CharacterWriteRequest>().FirstOrDefault();
                if (admission.Proof.FixedUniverse is { } universe && write?.UserInput is { } input
                    && !string.Equals(input.World?.Trim(), universe, StringComparison.Ordinal))
                {
                    return ApiProblem.Create(http, 409, "invitation_universe_required");
                }
                if (admission.Proof.FixedUniverse is { } fixedUniverse
                    && invocation.Arguments.OfType<CharacterCorrectionRequest>().Any())
                {
                    var current = await http.RequestServices.GetRequiredService<CharacterProfileStore>()
                        .ReadAsync(accountId, http.RequestAborted);
                    if (current is not null && current.UserInput.World.Trim() != fixedUniverse)
                    {
                        return ApiProblem.Create(http, 409, "invitation_universe_required");
                    }
                }
                if (http.Request.Method is "PUT" or "POST")
                {
                    var consentError = await AccountAiConsent.RequireAsync(http);
                    if (consentError is not null) { return consentError; }
                }
                return await next(invocation);
            }
            catch (CharacterException error) { return ApiProblem.Create(http, error.Status, error.Code); }
            catch (CustomerDataAccessException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        group.MapGet("", async (HttpContext http, CharacterProfileStore store) =>
        {
            var owner = AccountId(http);
            var profile = await store.ReadAsync(owner, http.RequestAborted);
            var wand = profile is not null && http.RequestServices.GetService<Kufr.Backend.Features.Wands.WandGenerationStore>() is { } wandStore
                    ? Kufr.Backend.Features.Wands.MobileWandSessions.Current(wandStore, owner, profile.ProfileId, profile.Revision) : null;
            var artwork = wand is null ? null : http.RequestServices.GetService<KeepsakeStore>()
                ?.FindOwnedWandArtwork(owner, wand.WandId);
            return Results.Ok(new { profile, generatedWand = wand?.ToPublic(), artwork });
        });
        group.MapPut("", async (CharacterWriteRequest request, HttpContext http, CharacterProfileStore store) =>
            Results.Ok(new { profile = await store.SaveAsync(AccountId(http), request with { OpenAiConsent = true, PortraitConsent = true }, http.RequestAborted, canEditExisting: AccountRoles.IsAdmin(http.User)) }));
        group.MapPost("/correction", async (CharacterCorrectionRequest request, HttpContext http, CharacterProfileStore store) =>
            AccountRoles.IsAdmin(http.User)
                ? Results.Ok(new { profile = await store.CorrectAsync(AccountId(http), request with { OpenAiConsent = true }, http.RequestAborted) })
                : ApiProblem.Create(http, 403, "account_admin_required"));
        group.MapDelete("", async (long expectedRevision, string expectedProfileId, HttpContext http, CharacterProfileStore store) =>
        {
            await store.DeleteAsync(AccountId(http), expectedRevision, expectedProfileId, http.RequestAborted);
            // The endpoint filter still holds identity admission here. The durable
            // reconciler performs cross-store cleanup only after that reservation is released.
            return Results.NoContent();
        });
    }

    private static string AccountId(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    internal static Task ReconcileDeletionsAsync(IServiceProvider services, CharacterProfileStore store, CancellationToken token)
    {
        var journal = services.GetService<OperationJournal>();
        var conversations = journal is null ? null : services.GetService<ConversationStore>();
        return store.ReplayDeletionsAsync((accountId, profileId) =>
        {
            // Case cleanup is mandatory and independent of the optional operation journal.
            // ReplayDeletionsAsync marks the tombstone complete only after this callback returns.
            Kufr.Backend.Features.SealedCase.SealedCaseErasure.EraseAccount(services, accountId);
            var wands = services.GetService<Kufr.Backend.Features.Wands.WandGenerationStore>();
            if (profileId is null) { wands?.DeleteAccount(accountId); }
            else { wands?.DeleteProfile(accountId, profileId); }
            if (journal is not null)
            {
                journal.RevokeCustomerProfile(accountId, profileId);
                foreach (var session in journal.CustomerSessions(accountId).Where(session =>
                    (profileId is null || session.ProfileId == profileId) && session.ConversationId is not null))
                { conversations!.InvalidateMemoryContext(session.DeviceId, session.ConversationId); }
            }
            return Task.CompletedTask;
        }, token);
    }
}

internal sealed class CharacterEnrichmentWorker(IServiceScopeFactory scopes, CharacterProfileStore store,
    ICharacterEnrichmentProvider provider, ILogger<CharacterEnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var reconciliationScope = scopes.CreateScope();
                var logs = reconciliationScope.ServiceProvider.GetService<ServerLogStore>();
                await CharacterEndpoints.ReconcileDeletionsAsync(reconciliationScope.ServiceProvider, store, stoppingToken);
                var job = await store.ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                using var jobTrace = new JobTelemetry("character.enrichment.process", job.Id, job.Attempt, job.TraceParent, job.CreatedAt);
                var activity = jobTrace.Activity;
                activity?.SetTag("app.phase", "authorization");
                logs?.Write("info", "character_job_started", operationId: Guid.Parse(job.Id).ToString(), attempt: job.Attempt);
                using var scope = scopes.CreateScope();
                var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var authority = scope.ServiceProvider.GetRequiredService<CustomerAdmissionAuthority>();
                var disabled = await accounts.Users.AsNoTracking().Where(user => user.Id == job.AccountId)
                    .Select(user => (bool?)user.Disabled).SingleOrDefaultAsync(stoppingToken);
                if (disabled is null)
                {
                    activity?.SetTag("app.outcome", "discarded");
                    await store.FenceAccountDeletionAsync(job.AccountId, stoppingToken);
                    continue;
                }
                if (disabled.Value)
                {
                    activity?.SetTag("app.outcome", "rejected");
                    await store.CompleteAsync(job, null, "account_unavailable", stoppingToken);
                    continue;
                }
                if (job.Attempt > 3)
                {
                    activity?.SetTag("app.outcome", "attempt_limit");
                    await store.CompleteAsync(job, null, "character_job_interrupted", stoppingToken);
                    continue;
                }
                try
                {
                    CharacterWorkLease work;
                    using (var admission = authority.AcquireAccount(job.AccountId, stoppingToken))
                    {
                        work = store.BeginAccountWork(job.AccountId, stoppingToken);
                    }
                    using var activeWork = work;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(activeWork.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                    using (var admission = authority.AcquireAccount(job.AccountId, deadline.Token))
                    {
                        if (!await store.CanDispatchAsync(job, deadline.Token))
                        { activity?.SetTag("app.outcome", "discarded"); continue; }
                    }
                    activity?.SetTag("app.phase", "provider");
                    var enrichment = await provider.EnrichAsync(job, deadline.Token);
                    // Recheck revocation after network work and before publishing a result.
                    disabled = await accounts.Users.AsNoTracking().Where(user => user.Id == job.AccountId)
                        .Select(user => (bool?)user.Disabled).SingleOrDefaultAsync(stoppingToken);
                    if (disabled is null)
                    {
                        activity?.SetTag("app.outcome", "discarded");
                        await store.FenceAccountDeletionAsync(job.AccountId, stoppingToken);
                        continue;
                    }
                    if (disabled.Value)
                    {
                        activity?.SetTag("app.outcome", "rejected");
                        await store.CompleteAsync(job, null, "account_unavailable", stoppingToken);
                        continue;
                    }
                    activity?.SetTag("app.phase", "persistence");
                    using var completionAdmission = authority.AcquireAccount(job.AccountId, stoppingToken);
                    var completed = await store.CompleteAsync(job, enrichment, null, stoppingToken);
                    activity?.SetTag("app.phase", "completed");
                    activity?.SetTag("app.outcome", completed ? "completed" : "discarded");
                    logs?.Write("info", completed ? "character_job_completed" : "character_job_discarded",
                        operationId: Guid.Parse(job.Id).ToString(), attempt: job.Attempt);
                }
                catch (OperationCanceledException error) when (stoppingToken.IsCancellationRequested)
                { BackendTelemetry.Failed(activity, error, stoppingToken); break; }
                catch (CustomerDataAccessException)
                {
                    activity?.SetTag("app.outcome", "discarded");
                }
                catch (Exception error)
                {
                    BackendTelemetry.Failed(activity, error, stoppingToken);
                    logger.LogWarning("Character enrichment failed for job {JobId} ({ErrorType}).", job.Id, error.GetType().Name);
                    var code = CharacterProviderFailure.Code(error);
                    activity?.SetTag("app.failure_code", code);
                    if (CharacterProviderFailure.IsTransient(error) && job.Attempt < 3)
                    {
                        var retried = await store.RetryAsync(job, stoppingToken);
                        activity?.SetTag("app.outcome", retried ? "retry_scheduled" : "discarded");
                        logs?.Write("warning", retried ? "character_job_retry_scheduled" : "character_job_discarded",
                            status: CharacterProviderFailure.Status(error), operationId: Guid.Parse(job.Id).ToString(), code: code, attempt: job.Attempt);
                        continue;
                    }
                    var failed = await store.CompleteAsync(job, null, code, stoppingToken);
                    logs?.Write("warning", failed ? "character_job_failed" : "character_job_discarded",
                        status: CharacterProviderFailure.Status(error), operationId: Guid.Parse(job.Id).ToString(), code: code, attempt: job.Attempt);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError("Character worker operation failed ({ErrorType}).", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
