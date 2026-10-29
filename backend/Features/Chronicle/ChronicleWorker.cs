using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.SealedCase;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Chronicle;

internal sealed class ChronicleWorker(ChronicleStore store, IChronicleIllustrator illustrator,
    IServiceScopeFactory scopes, ILogger<ChronicleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessNextAsync(stoppingToken)) { continue; }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("Chronicle job queue could not be read ({ErrorType}).", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken stoppingToken = default)
    {
            var lease = store.LeaseNext();
            if (lease is null) { return false; }
            var dispatched = false;
            try
            {
                using (var scope = scopes.CreateScope())
                {
                    var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                    var characters = scope.ServiceProvider.GetRequiredService<CharacterProfileStore>();
                    var subjects = lease.Event.Participants.Select(person => new SealedCaseSubject(person.AccountId, ChronicleStore.ProfileId(person))).ToArray();
                    using var admission = new SealedCasePrivacyAuthority(database.Database.GetConnectionString()!, characters).Acquire(subjects, null);
                    if (!store.MarkDispatched(lease)) { return true; }
                    dispatched = true;
                }
                var output = await illustrator.GenerateAsync(lease.Event, stoppingToken);
                store.Complete(lease, output);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                store.Fail(lease, "worker_stopped", dispatchUncertain: dispatched);
                throw;
            }
            catch (Exception exception)
            {
                store.Fail(lease, exception is InvalidDataException ? "illustration_invalid" : "illustration_result_unknown",
                    dispatchUncertain: dispatched);
                logger.LogWarning("Chronicle illustration {JobId} failed ({ErrorType}, attempt {Attempt}).",
                    lease.Id, exception.GetType().Name, lease.Attempts);
            }
            return true;
    }
}
