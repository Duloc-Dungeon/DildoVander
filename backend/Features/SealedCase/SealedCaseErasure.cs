using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.SealedCase;

internal static class SealedCaseErasure
{
    internal static void EraseAccount(IServiceProvider services, string accountId)
    {
        var store = services.GetService<SealedCaseStore>();
        if (store is null && services.GetService<AccountConfiguration>() is { } configuration)
        {
            var path = Path.Combine(configuration.DataRoot, "sealed-case.sqlite");
            // Turning off the feature must not acknowledge deletion while retaining
            // previously captured content. Do not create a database for unused features.
            if (File.Exists(path)) { store = new SealedCaseStore(path); }
        }
        store?.EraseAccount(accountId);
    }
}
