using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Launch;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Wands;

internal sealed class MobileWandWorkshopWorker(IServiceScopeFactory scopes, WandGenerationStore store,
    WandGenerationService generation, IMobileWandQuestionProvider questions,
    ILogger<MobileWandWorkshopWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                for (var count = 0; count < 8; count++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var question = store.ClaimWorkshopQuestion();
                    if (question is null) break;
                    await ProcessQuestionAsync(question, stoppingToken);
                }
                foreach (var job in store.PendingWorkshops())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ProcessAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("Mobile wand workshop queue pass failed ({ErrorType}).", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessQuestionAsync(MobileWandQuestionJob job, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var accounts = services.GetRequiredService<AccountDatabase>();
        if (!await HasActiveSessionAsync(accounts, job.AccountId, token))
        {
            store.ReleaseWorkshopQuestion(job);
            return;
        }
        if (!await QuestionAuthorityAsync(job, services, token))
        {
            store.FailWorkshopQuestion(job, "question_authoring_authority_unavailable");
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            var draft = await questions.AuthorAsync(job, timeout.Token);
            if (!await QuestionAuthorityAsync(job, services, timeout.Token))
            {
                store.FailWorkshopQuestion(job, "question_authoring_authority_unavailable");
                return;
            }
            store.CompleteWorkshopQuestion(job, draft.Question, draft.BasedOnFactIds, usedFallback: false);
        }
        catch (MobileWandQuestionAuthoringException exception) when (exception.SafeFallback)
        {
            var fallback = OpenAiMobileWandQuestionProvider.FallbackQuestion(job);
            store.CompleteWorkshopQuestion(job, fallback, [], usedFallback: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Workshop question {QuestionId} authoring failed ({Code}).",
                job.Step.QuestionId, exception is MobileWandQuestionAuthoringException authoring
                    ? authoring.Code : exception.GetType().Name);
            store.FailWorkshopQuestion(job, exception is MobileWandQuestionAuthoringException uncertain
                ? uncertain.Code : "question_authoring_uncertain");
        }
    }

    private static async Task<bool> QuestionAuthorityAsync(MobileWandQuestionJob job,
        IServiceProvider services, CancellationToken token)
    {
        if (!await CurrentAuthorityAsync(job.AccountId, job.LaunchGroupId, job.LaunchAccessRevision, services, token)) return false;
        if (await services.GetRequiredService<CharacterProfileStore>().CaptureAsync(job.AccountId,
            job.Profile.ProfileId, job.Profile.Revision, token) is null) return false;
        var boss = services.GetRequiredService<GameStore>().ReadBoss(job.LaunchGroupId, job.AccountId);
        return !boss.Revealed || boss.Rescued;
    }

    private static async Task<bool> CurrentAuthorityAsync(string accountId, string groupId,
        long accessRevision, IServiceProvider services, CancellationToken token)
    {
        var accounts = services.GetRequiredService<AccountDatabase>();
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId
            && !item.Disabled && item.LaunchGroupRevokedAt == null && item.LaunchGroupId == groupId
            && item.LaunchGroupAccessRevision == accessRevision
            && !accounts.DeletionRequests.Any(deletion => deletion.UserId == accountId), token);
        if (user is null || !AccountAiConsent.HasConsent(user)
            || !await HasActiveSessionAsync(accounts, accountId, token)) return false;
        var userManager = services.GetRequiredService<UserManager<AccountUser>>();
        var roles = await userManager.GetRolesAsync(user);
        if (!roles.Contains(AccountRoles.Customer, StringComparer.Ordinal)) return false;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, accountId), new(AccountIdentity.SessionClaim, "workshop-worker")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "workshop"));
        var access = await services.GetRequiredService<LaunchAccessService>().EvaluateAsync(principal);
        if (!access.Allowed || services.GetRequiredService<LaunchPolicyStore>().ReadGroup(groupId)?.OpenedAt is null)
            return false;
        return true;
    }

    private async Task ProcessAsync(MobileWandWorkshopJob job, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var accounts = services.GetRequiredService<AccountDatabase>();
        if (!await HasActiveSessionAsync(accounts, job.AccountId, token))
        {
            store.DeferWorkshop(job.AccountId, job.OperationId);
            return;
        }
        if (!await CurrentAuthorityAsync(job.AccountId, job.LaunchGroupId, job.LaunchAccessRevision, services, token))
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "workshop_authority_revoked");
            return;
        }
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == job.AccountId
            && !item.Disabled && !accounts.DeletionRequests.Any(deletion => deletion.UserId == job.AccountId), token);
        if (user is null || !AccountAiConsent.HasConsent(user))
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "account_or_consent_unavailable");
            return;
        }
        var profiles = services.GetRequiredService<CharacterProfileStore>();
        if (await profiles.CaptureAsync(job.AccountId, job.Profile.ProfileId, job.Profile.Revision, token) is null)
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "wand_profile_revision_mismatch");
            return;
        }

        if (user.LaunchGroupId is null
            || services.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId)?.OpenedAt is null)
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "game_group_inactive");
            return;
        }

        var game = services.GetRequiredService<GameStore>();
        var boss = game.ReadBoss(user.LaunchGroupId, job.AccountId);
        if (boss.Revealed && !boss.Rescued)
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "wand_workshop_boss_captured");
            return;
        }

        var session = store.Read(job.AccountId, job.OperationId);
        if (session is null)
        {
            session = store.Begin(job.AccountId, job.OperationId, job.Profile, 1,
                openAiConsent: true, outputLanguage: "cs");
        }

        if (session.Status == "queued" || session.Status == "unavailable" && job.RetryRequested)
        {
            session = await generation.GenerateAsync(job.AccountId, job.OperationId,
                ct => AuthorityAsync(job, services, ct), token, retry: session.Status == "unavailable",
                evidence: job.Steps.Select(StepEvidence).ToArray());
        }

        if (session.Status == "unavailable")
        {
            store.FailWorkshop(job.AccountId, job.OperationId, session.ErrorCode ?? "wand_generation_unavailable");
            return;
        }

        if (session.Status == "ready")
        {
            if (!await AuthorityAsync(job, services, token))
            {
                store.FailWorkshop(job.AccountId, job.OperationId, "wand_generation_stale");
                return;
            }
            var wand = store.Select(job.AccountId, job.OperationId, job.Profile.ProfileId,
                job.Profile.Revision, 1, currentReadyMask: 1);
            await IssueWandKeepsakeAsync(job, wand, services, token);
            return;
        }

        if (session.Status == "selected" && session.SelectedWand is { } selected)
            await IssueWandKeepsakeAsync(job, selected, services, token);
    }

    private static async Task<bool> AuthorityAsync(MobileWandWorkshopJob job, IServiceProvider services,
        CancellationToken token)
    {
        if (!await CurrentAuthorityAsync(job.AccountId, job.LaunchGroupId, job.LaunchAccessRevision, services, token)) return false;
        if (await services.GetRequiredService<CharacterProfileStore>().CaptureAsync(job.AccountId,
            job.Profile.ProfileId, job.Profile.Revision, token) is null) return false;
        var boss = services.GetRequiredService<GameStore>().ReadBoss(job.LaunchGroupId, job.AccountId);
        return !boss.Revealed || boss.Rescued;
    }

    private static Task<bool> HasActiveSessionAsync(AccountDatabase accounts, string accountId, CancellationToken token) =>
        accounts.Sessions.AsNoTracking().AnyAsync(session => session.UserId == accountId
            && session.ExpiresAt > AccountEndpoints.Now(), token);

    private async Task IssueWandKeepsakeAsync(MobileWandWorkshopJob job, GeneratedWandSnapshot wand,
        IServiceProvider services, CancellationToken token)
    {
        var ownerSession = await services.GetRequiredService<AccountDatabase>().Sessions.AsNoTracking()
            .Where(session => session.UserId == job.AccountId && session.ExpiresAt > AccountEndpoints.Now())
            .OrderByDescending(session => session.AuthenticatedAt).Select(session => session.Id).FirstOrDefaultAsync(token);
        var keepsakes = services.GetService<KeepsakeStore>();
        if (ownerSession is null || keepsakes is null || !RealArtworkConfigured(services.GetRequiredService<IConfiguration>()))
        {
            store.CompleteWorkshopWithoutArtwork(job.AccountId, job.OperationId,
                keepsakes is not null && !RealArtworkConfigured(services.GetRequiredService<IConfiguration>())
                    ? "wand_artwork_provider_unconfigured" : "wand_artwork_unavailable");
            return;
        }

        try
        {
            var issued = keepsakes.Issue(ArtworkSnapshot(wand), new(job.AccountId, ownerSession));
            store.CompleteWorkshop(job.AccountId, job.OperationId, issued.Id);
        }
        catch (KeepsakeException error)
        {
            // The selected WandId is durable already; artwork availability is a separate job.
            logger.LogWarning("Workshop wand {WandId} was saved but its artwork archive could not be opened ({Code}).",
                wand.WandId, error.Code);
            store.CompleteWorkshopWithoutArtwork(job.AccountId, job.OperationId, "wand_artwork_archive_unavailable");
        }
    }

    internal static bool RealArtworkConfigured(IConfiguration configuration) =>
        string.Equals(configuration["Keepsake:ProviderMode"], "openai", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"])
        && !string.IsNullOrWhiteSpace(configuration["Keepsake:ImageModel"]);

    internal static KeepsakeIssueSnapshot ArtworkSnapshot(GeneratedWandSnapshot wand) => new(
        "mobile-wand:" + wand.WandId, "mobile-wand-workshop", 1, wand.WandId, wand.Name,
        // Full immutable fiction is already in GeneratedWand. Duplicating it as
        // escaped JSON can exceed the archive's 4,000-character catalog limit.
        "Osobní hůlka vytvořená ze zvolené postavy a tří odpovědí.",
        "Osobní digitální hůlka vytvořená v mobilní dílně.", GeneratedWand: wand.ToPublic());

    internal static string StepEvidence(MobileWandWorkshopStep step) => System.Text.Json.JsonSerializer.Serialize(new
    {
        step.QuestionId, step.Question, step.AnswerId, step.AnswerRevision,
        answer = step.Answer, step.Skipped, step.SourceMode
    });
}
