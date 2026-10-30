namespace Kufr.Backend.Features.Wands;

internal static class WorkshopVoiceLimits
{
    // A conversation plus manufacture and the final verdict needs more than
    // three minutes. The provider and exclusive owner lease outlive the control
    // session so its last queued speech can finish without overlapping a caller.
    internal const int ConversationSeconds = 600;
    internal const int PlaybackTailSeconds = 120;
    internal const int ProviderSeconds = ConversationSeconds + PlaybackTailSeconds;
    internal const int LeaseSeconds = ProviderSeconds + 30;
}
