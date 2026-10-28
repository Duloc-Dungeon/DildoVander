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
    WandGenerationService generation, ILogger<MobileWandWorkshopWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await Parallel.ForEachAsync(store.PendingWorkshops(), new ParallelOptions
                { MaxDegreeOfParallelism = 3, CancellationToken = stoppingToken }, async (job, token) =>
                    await ProcessAsync(job, token));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("Mobile wand workshop queue pass failed ({ErrorType}).", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static async Task<bool> CurrentAuthorityAsync(string accountId, string groupId,
        long accessRevision, int tier, IServiceProvider services, CancellationToken token)
    {
        var accounts = services.GetRequiredService<AccountDatabase>();
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId
            && !item.Disabled
            && !accounts.DeletionRequests.Any(deletion => deletion.UserId == accountId), token);
        if (user is null || !AccountAiConsent.HasConsent(user)
            || !await HasActiveSessionAsync(accounts, accountId, token)) return false;
        var userManager = services.GetRequiredService<UserManager<AccountUser>>();
        var roles = await userManager.GetRolesAsync(user);
        var isOwner = roles.Contains(AccountRoles.Owner, StringComparer.Ordinal);
        if (!roles.Contains(AccountRoles.Customer, StringComparer.Ordinal) && !isOwner) return false;
        var ownerPreparation = isOwner && tier == 1;
        if (!ownerPreparation && (user.LaunchGroupRevokedAt is not null || user.LaunchGroupId != groupId
            || user.LaunchGroupAccessRevision != accessRevision)) return false;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, accountId), new(AccountIdentity.SessionClaim, "workshop-worker")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "workshop"));
        var access = await services.GetRequiredService<LaunchAccessService>().EvaluateAsync(principal);
        var group = services.GetRequiredService<LaunchPolicyStore>().ReadGroup(groupId);
        if (!access.Allowed || group is null || group.OpenedAt is null && !ownerPreparation)
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
        if (!await CurrentAuthorityAsync(job.AccountId, job.LaunchGroupId, job.LaunchAccessRevision, job.Tier, services, token))
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
        if (await profiles.CapturePreparedAsync(job.AccountId, job.Profile.ProfileId, job.Profile.Revision, token) is null)
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "wand_profile_revision_mismatch");
            return;
        }

        if (user.LaunchGroupId is null)
        {
            store.FailWorkshop(job.AccountId, job.OperationId, "game_group_inactive");
            return;
        }

        var game = services.GetRequiredService<GameStore>();
        var boss = game.ReadBoss(job.LaunchGroupId, job.AccountId);
        if (boss.Revealed && !boss.Rescued)
        {
            store.PauseWorkshopForBoss(job.AccountId, job.OperationId);
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
                ct => ContextAuthorityAsync(job, services, ct), token, retry: session.Status == "unavailable",
                evidence: GenerationEvidence(job));
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
                if (await ContextAuthorityAsync(job, services, token))
                    store.PauseWorkshopForBoss(job.AccountId, job.OperationId);
                else
                    store.FailWorkshop(job.AccountId, job.OperationId, "wand_generation_stale");
                return;
            }
            var wand = store.Select(job.AccountId, job.OperationId, job.Profile.ProfileId,
                job.Profile.Revision, 1, currentReadyMask: 1);
            await IssueWandKeepsakeAsync(job, wand, services, token);
            return;
        }

        if (session.Status == "selected" && session.SelectedWand is { } selected)
        {
            if (!await AuthorityAsync(job, services, token))
            {
                if (await ContextAuthorityAsync(job, services, token))
                    store.PauseWorkshopForBoss(job.AccountId, job.OperationId);
                return;
            }
            await IssueWandKeepsakeAsync(job, selected, services, token);
        }
    }

    private static async Task<bool> AuthorityAsync(MobileWandWorkshopJob job, IServiceProvider services,
        CancellationToken token)
    {
        if (!await ContextAuthorityAsync(job, services, token)) return false;
        return !IsBossCaptive(job.LaunchGroupId, job.AccountId, services);
    }

    private static async Task<bool> ContextAuthorityAsync(MobileWandWorkshopJob job,
        IServiceProvider services, CancellationToken token) =>
        await CurrentAuthorityAsync(job.AccountId, job.LaunchGroupId, job.LaunchAccessRevision, job.Tier, services, token)
        && await services.GetRequiredService<CharacterProfileStore>().CapturePreparedAsync(job.AccountId,
            job.Profile.ProfileId, job.Profile.Revision, token) is not null;

    private static bool IsBossCaptive(string groupId, string accountId, IServiceProvider services)
    {
        var boss = services.GetRequiredService<GameStore>().ReadBoss(groupId, accountId);
        return boss.Revealed && !boss.Rescued;
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
        && OpenAiKeepsakeProvider.SupportsTransparentWandArtwork(configuration["Keepsake:ImageModel"]);

    internal static KeepsakeIssueSnapshot ArtworkSnapshot(GeneratedWandSnapshot wand) => new(
        "mobile-wand:" + wand.WandId, "mobile-wand-workshop", 1, wand.WandId, wand.Name,
        // Full immutable fiction is already in GeneratedWand. Duplicating it as
        // escaped JSON can exceed the archive's 4,000-character catalog limit.
        "Osobní hůlka vytvořená ze zvolené postavy a rozhovoru.",
        "Osobní digitální hůlka vytvořená v mobilní dílně.", GeneratedWand: wand.ToPublic());

    internal static string StepEvidence(MobileWandWorkshopStep step) => System.Text.Json.JsonSerializer.Serialize(new
    {
        step.QuestionId, step.Question, step.AnswerId, step.AnswerRevision,
        answer = step.Answer, step.Skipped, step.SourceMode
    });

    internal static string[] GenerationEvidence(MobileWandWorkshopJob job)
    {
        if (job.Messages.Length > 0)
            return job.Messages.OrderBy(message => message.CreatedAt).Select(message =>
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    message.MessageId, message.Revision, message.Role, message.Text, message.SourceMode
                })).ToArray();
        return job.Steps.Select(StepEvidence).ToArray();
    }
}
