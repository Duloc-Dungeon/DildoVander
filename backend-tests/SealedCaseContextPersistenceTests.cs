using System.Text.Json;
using Kufr.Backend.Features.SealedCase;

internal static class SealedCaseContextPersistenceTests
{
    internal static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "sealed-context-" + Guid.NewGuid().ToString("N"), "game.sqlite");
        try
        {
            var store = new SealedCaseStore(path);
            var c = store.Create("group", "account-A", 100, 8, "first");
            var initial = store.InitializeWorld(c.Id, "group", 1, 101,
                new SealedGame([new("A", knownPlayers: ["B"]), new("B", knownPlayers: ["A"])], "private-seed"));
            var authority = new SealedContextAuthority(c.Id, "group", [
                new("account-A", "A", [SealedContextRole.Player, SealedContextRole.PrivateNarrator]),
                new("account-B", "B", [SealedContextRole.Player]),
                new("engine", null, [SealedContextRole.Engine])]);
            var resolution = store.ResolveWorldChapter(c.Id, "group", 1, initial.Revision, 102,
                [new("own-order", "A", 1, SealedIntent.Hold), new("other-order", "B", 1, SealedIntent.Rest)]);
            var reopened = new SealedCaseStore(path);
            var frame = reopened.ReadContext(c.Id, "group", "account-A", SealedContextRole.Player, authority);
            var invalidBinding = authority with { Members = [new("account-A", "not-in-this-world", [SealedContextRole.Player])] };
            try { reopened.ReadContext(c.Id, "group", "account-A", SealedContextRole.Player, invalidBinding); throw new InvalidOperationException("unbound identity granted"); }
            catch (SealedCasePersistenceException e) { Check(e.Status == 404 && e.Code == "case_not_found", "identity must belong to the canonical case roster"); }
            var deniedProof = false;
            try
            {
                reopened.ReadContext(c.Id, "group", "account-A", SealedContextRole.Player, authority,
                    () => throw new SealedCasePersistenceException("account_access_unavailable", 401));
            }
            catch (SealedCasePersistenceException e) { deniedProof = e.Status == 401; }
            Check(deniedProof, "revoked live admission cannot return a projection");
            Check(frame.Context.CovertActions.Single().ActionRef == "own-order", "own persisted command is projected");
            Check(frame.Context.CovertActions.Single().ExecutionStatus == "RESOLVED_RESULT_NOT_OBSERVED"
                && frame.Context.CovertActions.Single().PerceivedResultRefs.Count == 0,
                "canonical chapter commit acknowledges processing of own command, not observation of its result");
            Check(frame.AllowedTargetIds.SequenceEqual(["B"]), "targets are persisted own knowledge");
            var json = frame.ToSchema().ToString();
            Check(!json.Contains("private-seed", StringComparison.Ordinal) && !json.Contains("other-order", StringComparison.Ordinal)
                && !json.Contains("NO_CONFIRMED_ACTION", StringComparison.Ordinal), "no seed, other order, internal reason");
            var engine = reopened.ReadContext(c.Id, "group", "engine", SealedContextRole.Engine, authority);
            Check(engine.Context.WorldTruth.EventRefs.SequenceEqual(resolution.Events.Select(x => x.GetProperty("id").GetString()!)), "truth references are canonical B1 persisted event IDs");
            foreach (var request in new[] { (c.Id, "wrong", authority), ("missing", "group", authority),
                (c.Id, "group", authority with { CaseId = "other-case" }) })
            {
                try { reopened.ReadContext(request.Item1, request.Item2, "account-A", SealedContextRole.Player, request.Item3); throw new InvalidOperationException("access granted"); }
                catch (SealedCasePersistenceException e) { Check(e.Status == 404 && e.Code == "case_not_found", "uniform denied persistence read"); }
            }
            var exported = Environment.GetEnvironmentVariable("SEALED_CONTEXT_EXPORT");
            if (exported is not null) File.WriteAllText(exported, JsonSerializer.Serialize(new[] { frame.ToSchema(), engine.ToSchema() }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
        Console.WriteLine("SealedCase canonical persistence -> authorized context checks passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("SealedCase context persistence: " + message); }
}
