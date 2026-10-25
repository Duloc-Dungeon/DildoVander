using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

internal static class TestingCharacterRaceTests
{
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"testing-consent-race-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["Backend:DatabasePath"]=Path.Combine(root,"backend.sqlite")}).Build();
        try
        {
            var provider=new HeldEnrichment();
            using var resources=new TestingResources(config,TimeProvider.System,null,null,provider,null);
            var lab=new PersonaLabStore(Path.Combine(root,"lab.sqlite"),LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId,new GptLiveOptions()));
            using var live=new GptLiveClient(config);using var realtime=new OpenAiRealtimeClient(config);
            using var runtime=new PersonaLabRuntime(lab,live,realtime);
            using var hardware=new TestingHardwareAdapter(config);
            var service=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            var character=await service.SaveCharacterAsync("owner",null,new(new("Original","Library"),true,true),default);
            var pending=await service.EnrichAsync("owner",character.Id,true,default);
            if(pending.Profile.Status!="queued")throw new InvalidOperationException("Enrichment request did not return while queued.");
            var processing=resources.ProcessNextCharacterEnrichmentAsync(default);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var withdrawn=await service.SaveCharacterAsync("owner",character.Id,
                new(new("Corrected","Library"),false,false,character.Profile.Revision),default);
            provider.Continue.SetResult();
            await processing;
            var current=service.Characters("owner").Single();
            if(current.LongTermConsent || current.Profile.Revision<withdrawn.Profile.Revision || current.Profile.UserInput.Name!="Corrected")
                throw new InvalidOperationException("Late enrichment restored withdrawn consent or stale profile.");
            var inspecting=await service.SaveCharacterAsync("inspector",null,new(new("Mira","Fictional library"),true),default);
            var session=service.Create("inspector",new(OpenAiConsent:true,RecordContent:true,OperationId:Guid.NewGuid().ToString()));
            var selected=await service.SelectCharacterAsync("inspector",session.Id,new(inspecting.Id,session.Revision),default);
            if(selected.CharacterSnapshot?.Enrichment is not null)throw new InvalidOperationException("Queued snapshot fabricated generated data.");
            await resources.ProcessNextCharacterEnrichmentAsync(default);
            // Deliberately skip CharactersAsync: completion must be visible from
            // the authoritative detail endpoint without collection polling.
            var completed=await service.DetailAsync("inspector",session.Id,default);
            if(completed.LatestCharacter?.Profile.Status!="ready"||completed.LatestCharacter.Profile.Enrichment is null
                ||completed.CharacterSnapshot?.Enrichment is not null||completed.Character?.Profile.Enrichment is not null)
                throw new InvalidOperationException("Latest generated output and frozen snapshot were conflated or hidden.");
            var recaptured=await service.SelectCharacterAsync("inspector",session.Id,new(inspecting.Id,selected.Session.Revision),default);
            if(recaptured.CharacterSnapshot?.Enrichment?.Summary!="Fictional context"
                ||recaptured.CharacterSnapshot.Context!=recaptured.Conversation.Testing?.CharacterContext)
                throw new InvalidOperationException("Waiting/reselecting before the first turn did not capture actual persisted enrichment and context.");
            var resumed=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            var reopened=await resumed.DetailAsync("inspector",session.Id,default);
            if(System.Text.Json.JsonSerializer.Serialize(reopened.CharacterSnapshot)!=System.Text.Json.JsonSerializer.Serialize(recaptured.CharacterSnapshot)
                ||reopened.LatestCharacter?.Profile.Enrichment is null)
                throw new InvalidOperationException("Inspector snapshot and output did not survive reopening.");
            var other=await service.SaveCharacterAsync("inspector",null,new(new("Other character","Different world")),default);
            var raceSession=service.Create("inspector",new(OpenAiConsent:true,RecordContent:true,OperationId:Guid.NewGuid().ToString()));
            var raceSelected=await service.SelectCharacterAsync("inspector",raceSession.Id,new(inspecting.Id,raceSession.Revision),default);
            // Hold the real adapter's I/O gate to deterministically interleave
            // the GET with a second client's selection, without production hooks.
            var getHost=typeof(TestingHardwareAdapter).GetMethod("Get",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            var host=getHost.Invoke(hardware,[raceSession.Id,raceSession.VirtualDeviceId])!;
            var hardwareGate=(SemaphoreSlim)host.GetType().GetField("Gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
            await hardwareGate.WaitAsync();
            Task<TestingSessionDetail> heldRead;
            Task<TestingSessionDetail> changedSelection;
            try
            {
                heldRead=service.DetailAsync("inspector",raceSession.Id,default);
                changedSelection=service.SelectCharacterAsync("inspector",raceSession.Id,new(other.Id,raceSelected.Session.Revision),default);
                if(service.Read("inspector",raceSession.Id).CharacterId!=other.Id)
                    throw new InvalidOperationException("The selection did not cross the held detail read.");
            }
            finally { hardwareGate.Release(); }
            await changedSelection;
            var coherent=await heldRead;
            if(coherent.CharacterSnapshot?.UserInput.Name!="Other character"
                ||!coherent.CharacterSnapshot.Context.Contains("Other character")||coherent.LatestCharacter?.Id!=other.Id)
                throw new InvalidOperationException("Concurrent detail mixed old frozen identity with new conversation context.");
            await service.DeleteCharacterAsync("inspector",inspecting.Id,default);
            var archived=await service.DetailAsync("inspector",session.Id,default);
            if(archived.Session.Status!="ended"||archived.LatestCharacter is not null||archived.CharacterSnapshot?.Enrichment is null)
                throw new InvalidOperationException("Deleting the latest profile made the ended frozen conversation unreadable.");
            Console.WriteLine("Testing character enrichment race preserves withdrawn consent and latest profile (controlled provider).");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private sealed class HeldEnrichment:ICharacterEnrichmentProvider
    {
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CharacterEnrichment> EnrichAsync(CharacterJob job,CancellationToken token)
        {
            Started.TrySetResult();await Continue.Task;
            return new(job.UserInput.Name,job.UserInput.World,"Fictional context",[],[],[],false,[],"controlled-race-provider","test-v1",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),job.Revision,"synthetic");
        }
    }
}
