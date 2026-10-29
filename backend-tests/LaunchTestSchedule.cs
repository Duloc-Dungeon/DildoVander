using Kufr.Backend.Features.Launch;

internal static class LaunchTestSchedule
{
    internal static BetaAdmissionConfiguration Set(LaunchPolicyStore store, string groupId, long? launchAt, long now)
    {
        var admission = store.ReadBetaAdmission();
        var group = store.ReadGroup(groupId)!;
        return store.UpdateBetaAdmission("fixture",
            new(admission.Revision, groupId, admission.RecruitmentOpen, admission.FixedUniverse, group.Revision, launchAt), now);
    }
}
