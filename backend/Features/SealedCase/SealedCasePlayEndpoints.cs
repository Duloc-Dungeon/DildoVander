using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedPlanRequest(string OperationId, SealedPlanConfirmation Plan);
internal sealed record SealedOperationRequest(string OperationId);
internal sealed record SealedStatementRequest(string OperationId, string Body, string[] Recipients, bool Public);
internal sealed record SealedVoteRequest(string OperationId, int Round, string[] Nominees);

internal static partial class SealedCaseEndpoints
{
    private static void MapPlayableRoutes(RouteGroupBuilder routes)
    {
        string Actor(HttpContext h) => h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        string Group(HttpContext h) => (string)h.Items[GroupItem]!;
        CustomerAdmissionGuard Admit(HttpContext h) => AuthorizeLobbyWrite(h);
        object Ack(string operationId, bool replayed) => new { operationId, accepted = true, replayed };
        routes.MapGet("/current", (HttpContext h, SealedCaseStore store) => Results.Ok(new { id = store.LatestCase(Group(h)) }));
        routes.MapGet("/{id}/play", (string id, HttpContext h, SealedCaseStore store) =>
        {
            store.AdvancePlayableCase(id, Group(h), Actor(h), () => Admit(h));
            return Results.Ok(store.ReadPlay(id, Group(h), Actor(h), () => Admit(h)));
        });
        routes.MapGet("/{id}/context", (string id, HttpContext h, SealedCaseStore store) =>
            Results.Ok(store.ReadContext(id, Group(h), Actor(h), SealedContextRole.Player,
                store.ReadRosterAuthority(id, Group(h)), () => DemandPlayerAdmissionForContext(h)).ToSchema()));
        routes.MapPost("/{id}/start", (string id, SealedCaseStartRequest request, HttpContext h,
            SealedCaseStore store, AccountDatabase db, CharacterProfileStore profiles) =>
        {
            ValidateOperation(request.OperationId);
            return Results.Ok(store.StartPlayableCase(id, Group(h), Actor(h), request.OperationId!, request.ExpectedRevision,
                () => Admit(h), () => EligibleMembers(Group(h), db, profiles)));
        });
        routes.MapPost("/{id}/scenario-consent", (string id, SealedOperationRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.AcceptScenario(id, Group(h), Actor(h), request.OperationId, () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/actions", (string id, SealedPlanRequest request, HttpContext h, SealedCaseStore store) =>
        {
            ValidateOperation(request.OperationId);
            if (request.Plan is null) { return ApiProblem.Create(h, 400, "sealed_case_invalid_plan"); }
            // Other players' private writes must not become a client-visible CAS
            // oracle. Re-read/retry internal case CAS; own action revision remains strict.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var record = store.Read(id);
                var expected = store.OperationRevision(id, Actor(h), request.OperationId) ?? record.Revision;
                try
                {
                    var receipt = store.ConfirmPlan(id, Group(h), Actor(h), request.OperationId, expected, request.Plan, () => Admit(h));
                    return Results.Ok(new { request.OperationId, accepted = true, receipt.Replayed, actionRevision = request.Plan.ExpectedActionRevision + 1 });
                }
                catch (SealedCasePersistenceException error) when (error.Code == "revision_conflict") { }
            }
            return ApiProblem.Create(h, 503, "sealed_case_retry");
        });
        routes.MapPost("/{id}/pause", (string id, SealedOperationRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.ChangePause(id, Group(h), Actor(h), request.OperationId, false, () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/resume", (string id, SealedOperationRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.ChangePause(id, Group(h), Actor(h), request.OperationId, true, () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/statements", (string id, SealedStatementRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.PostStatement(id, Group(h), Actor(h), request.OperationId, request.Body ?? "", request.Recipients ?? [], request.Public, () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/ballots", (string id, SealedVoteRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.Vote(id, Group(h), Actor(h), request.OperationId, new(request.Round, request.Nominees ?? []), () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/oversight", (string id, SealedOversightRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.RecommendOversight(id, Group(h), Actor(h), request.OperationId, request.Chapter, request.Nominee, () => Admit(h)).Replayed)));
        routes.MapPost("/{id}/disconnect", (string id, SealedDisconnectRequest request, HttpContext h, SealedCaseStore store)
            => Results.Ok(Ack(request.OperationId, store.Disconnect(id, Group(h), Actor(h), request.OperationId, request.PlayerId, () => Admit(h)).Replayed)));
        routes.MapGet("/{id}/moderator", (string id, HttpContext h, SealedCaseStore store)
            => Results.Ok(store.ReadModerator(id, Group(h), Actor(h), () => Admit(h))));
        routes.MapGet("/{id}/portraits/{player}", (string id, string player, HttpContext h, SealedCaseStore store) =>
        {
            var image = store.ReadCasePortrait(id, Group(h), Actor(h), player, () => Admit(h));
            return image is null ? Results.NotFound() : Results.File(image, "image/png", enableRangeProcessing: false);
        });
    }

    private static CustomerAdmissionGuard DemandPlayerAdmissionForContext(HttpContext h) => AuthorizeLobbyWrite(h);
}

internal sealed partial class SealedCaseStore
{
    internal long? OperationRevision(string caseId, string actor, string operationId)
    {
        using var connection = Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT expected_revision FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
        query.Parameters.AddWithValue("$case", caseId); query.Parameters.AddWithValue("$actor", actor); query.Parameters.AddWithValue("$key", operationId);
        return query.ExecuteScalar() is long revision ? revision : null;
    }

    internal SealedCaseActionReceipt AcceptScenario(string caseId, string group, string actor, string operationId, Func<CustomerAdmissionGuard> authorize)
        => AppendSocial(caseId, group, actor, operationId, "scenario-consent", new { accepted = true, policy = "pod-peceti-v1" }, authorize,
            (_, _, _) => new { accepted = true, policy = "pod-peceti-v1" });

    internal SealedTimingReceipt ChangePause(string caseId, string group, string actor, string operationId, bool resume, Func<CustomerAdmissionGuard> authorize)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // A retry of a pause operation uses its original expected revision,
            // even if messages or a completed resume have advanced the case.
            var revision = Read(caseId).Revision;
            using (var connection = Open())
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT expected_revision FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
                query.Parameters.AddWithValue("$case", caseId); query.Parameters.AddWithValue("$actor", actor); query.Parameters.AddWithValue("$key", operationId);
                if (query.ExecuteScalar() is long original) { revision = original; }
            }
            try
            {
                return resume ? ResumeTimeline(caseId, group, actor, operationId, revision, () => DemandPlayerAdmission(authorize, actor, group))
                    : PauseTimeline(caseId, group, actor, operationId, revision, () => DemandPlayerAdmission(authorize, actor, group));
            }
            catch (SealedCasePersistenceException error) when (error.Code == "revision_conflict") { }
        }
        throw new SealedCasePersistenceException("sealed_case_retry", 503);
    }

    internal byte[]? ReadCasePortrait(string caseId, string group, string actor, string player, Func<CustomerAdmissionGuard> authorize)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, () => DemandPlayerAdmission(authorize, actor, group), actor);
        _ = ReadCaseRevision(connection, transaction, caseId, group);
        var members = RosterMembers(connection, transaction, caseId);
        if (members.Length > 0 && members.All(m => m.AccountId != actor)) { throw new SealedCasePersistenceException("case_not_found", 404); }
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT image FROM sealed_case_private_snapshots WHERE case_id=$case AND subject_account=$player";
        query.Parameters.AddWithValue("$case", caseId); query.Parameters.AddWithValue("$player", player);
        var bytes = query.ExecuteScalar() as byte[]; transaction.Commit(); return bytes;
    }
}
