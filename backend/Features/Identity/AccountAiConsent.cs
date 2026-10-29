using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.Identity;

internal sealed record AccountAiConsentRequest(bool OpenAiConsent, int ConsentVersion = 1);

/// <summary>Account-scoped processing choice; registration and later AI requests share this evidence.</summary>
internal static class AccountAiConsent
{
    // Version 2 explicitly covers OpenAI and ElevenLabs. Older evidence never
    // silently authorizes the newly named processor, including old app clients.
    internal const int CurrentVersion = 2;
    internal static bool HasConsent(AccountUser user) => user.OpenAiConsentAt is > 0
        && user.OpenAiConsentVersion == CurrentVersion;
    internal static bool HasConsent(AccountCeremony ceremony) => ceremony.OpenAiConsentAt is > 0
        && ceremony.OpenAiConsentVersion == CurrentVersion;

    internal static async Task<bool> HasConsentAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        var user = await context.RequestServices.GetRequiredService<UserManager<AccountUser>>().GetUserAsync(context.User);
        return user is not null && !user.Disabled && HasConsent(user);
    }

    internal static async Task<IResult?> RequireAsync(HttpContext context) =>
        await HasConsentAsync(context) ? null : ApiProblem.Create(context, 409, "ai_consent_required");

    internal static async Task<IResult> AcceptAsync(AccountAiConsentRequest request, HttpContext context,
        UserManager<AccountUser> users)
    {
        if (!request.OpenAiConsent || request.ConsentVersion != CurrentVersion)
        { return ApiProblem.Create(context, 409, "ai_consent_required"); }
        var user = await users.GetUserAsync(context.User);
        if (user is null || user.Disabled) return ApiProblem.Create(context, 401, "account_required");
        if (!HasConsent(user))
        {
            user.OpenAiConsentAt = AccountEndpoints.Now();
            user.OpenAiConsentVersion = CurrentVersion;
            if (!(await users.UpdateAsync(user)).Succeeded)
                return ApiProblem.Create(context, 409, "account_consent_not_saved");
        }
        return Results.NoContent();
    }
}
