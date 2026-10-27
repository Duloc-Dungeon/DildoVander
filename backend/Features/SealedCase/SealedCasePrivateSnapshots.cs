using System.Text.Json;
using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.SealedCase;

// Private, removable case copy. Only its opaque Reference may enter append-only history.
internal sealed record SealedCasePrivateSnapshot(string Reference, string SubjectAccount, string ProfileId,
    long ProfileRevision, string ProfileJson, string? PortraitJson, byte[]? Image)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static SealedCasePrivateSnapshot From(string reference, string subjectAccount, CharacterProfile profile,
        CharacterPortraitCapture? portrait) => new(reference, subjectAccount, profile.ProfileId, profile.Revision,
            JsonSerializer.Serialize(profile, Json), portrait is null ? null : JsonSerializer.Serialize(portrait.Portrait, Json),
            portrait?.Image?.ToArray());
}
