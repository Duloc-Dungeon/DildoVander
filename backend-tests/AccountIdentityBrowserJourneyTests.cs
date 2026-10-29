using System.Net.Http.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static partial class AccountIdentityTests
{
    public static async Task RunGuestJourneyBrowserFixtureAsync()
    {
        var portrait = new FallbackPortraitGenerator
        {
            Fail = false,
            Image = await File.ReadAllBytesAsync("mobilni-aplikace/tests/fixtures/characters/matylda.webp")
        };
        var fallback = new FallbackPortraitLookup();
        await using var fixture = await Fixture.StartAsync(browserMode: true,
            customerFeatures: true, betaRecruitmentOpen: false, configureServices: services =>
            {
                // Override both image paths so this fixture never searches the network.
                services.RemoveAll<ICharacterPortraitProvider>();
                services.AddSingleton<ICharacterPortraitProvider>(portrait);
                services.RemoveAll<ICharacterPortraitFallbackProvider>();
                services.AddSingleton<ICharacterPortraitFallbackProvider>(fallback);
            });
        var origin = fixture.App.Urls.Single().Replace("127.0.0.1", "localhost");
        using var owner = fixture.Client();
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", origin);
        using var ownerKey = new VirtualPasskey(origin);
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Fixture owner");
        await CsrfAsync(owner);

        // A future opening keeps this journey in the real waiting-room branch.
        var launch = fixture.App.Services.GetRequiredService<LaunchPolicyStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var admission = launch.ReadBetaAdmission();
        var selected = launch.CreateOrSelectBetaGroup("guest-browser-fixture",
            new(admission.Revision), now);
        var production = launch.Read();
        launch.Update("guest-browser-fixture", new(production.Revision, production.BetaEnabled, production.BetaLaunchAt,
            production.BetaUnlocked, true, now + 86_400_000, false, production.TimeZone, production.Teaser,
            production.InitialLoadingMessage, production.PersonalizedLoadingEnabled, production.PersonalizedLoadingPrompt), now);
        launch.UpdateBetaAdmission("guest-browser-fixture",
            new(selected.Admission.Revision, selected.Group.Id, true, null), now);
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", recoveryUserId = (string?)null, launchGroupId = selected.Group.Id }));

        var deliveryRoot = Path.Combine(fixture.App.Services.GetRequiredService<AccountConfiguration>().DataRoot,
            "guest-delivery");
        Directory.CreateDirectory(deliveryRoot);
        var delivery = Path.Combine(deliveryRoot, "guest.txt");
        await File.WriteAllTextAsync(delivery,
            origin + "/signin#invite=" + invitation.GetProperty("token").GetString() + Environment.NewLine);
        Console.WriteLine("ACCOUNT_BROWSER_DELIVERY " + delivery);
        Console.WriteLine("ACCOUNT_BROWSER_FIXTURE " + origin);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync(cancellation.Token) is { } line)
                {
                    if (line == "stop") { cancellation.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
