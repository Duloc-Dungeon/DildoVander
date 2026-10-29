using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.SealedCase;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Chronicle;

// Preserve identity -> character reservations through response execution,
// including image streaming, so accepted deletion fences every shared copy.
internal sealed class ChroniclePrivacyResult(IResult inner, string owner, IReadOnlyList<string> eventIds) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<ChronicleStore>();
        var database = context.RequestServices.GetRequiredService<AccountDatabase>();
        var characters = context.RequestServices.GetRequiredService<CharacterProfileStore>();
        var subjects = store.SecuritySubjects(owner, eventIds);
        if (subjects is null) { await Denied(context); return; }
        try
        {
            using var admission = new SealedCasePrivacyAuthority(database.Database.GetConnectionString()!, characters).Acquire(subjects, null);
            if (store.SecuritySubjects(owner, eventIds) is null
                || !await database.Users.AsNoTracking().AnyAsync(user => user.Id == owner && !user.Disabled, context.RequestAborted))
            { await Denied(context); return; }
            await inner.ExecuteAsync(context);
        }
        catch (SealedCasePersistenceException error) when (error.Status == 404) { await Denied(context); }
        catch (Kufr.Backend.Features.CustomerData.CustomerDataAccessException)
        {
            await ApiProblem.Create(context, 409, "chronicle_privacy_unavailable").ExecuteAsync(context);
        }
    }

    private static Task Denied(HttpContext context) => ApiProblem.Create(context, 404, "chronicle_entry_unavailable").ExecuteAsync(context);

    internal static async ValueTask<object?> Filter(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var result = await next(invocation);
        if (result is not IResult inner || result is ChronicleExportResult
            || result is IStatusCodeHttpResult { StatusCode: >= 400 }) { return result; }
        var owner = invocation.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner is null) { return result; }
        var store = invocation.HttpContext.RequestServices.GetRequiredService<ChronicleStore>();
        var ids = result switch
        {
            IValueHttpResult { Value: ChroniclePage page } => page.Items.Select(item => item.Id)
                .Concat(page.Badges.Select(badge => badge.EventId)).Distinct(StringComparer.Ordinal).ToArray(),
            IValueHttpResult { Value: ChronicleEventView item } => [item.Id],
            _ when invocation.HttpContext.Request.RouteValues["id"]?.ToString() is { } id => [id],
            _ => store.OwnedEventIds(owner)
        };
        return new ChroniclePrivacyResult(inner, owner, ids);
    }
}

internal sealed class ChronicleExportResult(string owner) : IResult
{
    public Task ExecuteAsync(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<ChronicleStore>();
        var ids = store.OwnedEventIds(owner);
        // Build only from the exact admitted event set, inside the reservations.
        // New events cannot smuggle unreserved subjects into this export.
        return new ChroniclePrivacyResult(new DeferredExport(store, owner, ids), owner, ids).ExecuteAsync(context);
    }

    private sealed class DeferredExport(ChronicleStore store, string owner, IReadOnlyCollection<string> ids) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => Results.Text(store.ExportAccount(owner, ids),
            "application/json; charset=utf-8").ExecuteAsync(context);
    }
}
