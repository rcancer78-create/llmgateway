using System.Reflection;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class ProfileGenerationCaptureReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileChangedBeforeObservationCannotPublishOldSnapshot(bool changeDirectory)
    {
        using var fixture = new Fixture();
        Assert.Equal("old-model", Assert.Single(await fixture.Gateway.GetModelsAsync(true)).NativeModel);
        fixture.Store.Arm();
        var racing = Task.Run(() => fixture.Gateway.GetModelsAsync(true));
        try
        {
            await fixture.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Gateway.UpdateAccountAsync(fixture.NewProfile(changeDirectory));
        }
        finally { fixture.Store.Release.TrySetResult(); }
        var raced = await racing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(raced, m => m.NativeModel == "old-model");
        Assert.Equal("Owned new", fixture.Store.Find("owned")!.DisplayName);
        Assert.Equal("new-model", Assert.Single(await fixture.Gateway.GetModelsAsync(false)).NativeModel);
        Assert.Equal("new-model", Assert.Single(await fixture.Gateway.GetModelsAsync(true)).NativeModel);
    }

    [Fact]
    public async Task UnchangedProfileCatalogRemainsCached()
    {
        using var fixture = new Fixture();
        Assert.Equal("old-model", Assert.Single(await fixture.Gateway.GetModelsAsync(true)).NativeModel);
        Assert.Equal("old-model", Assert.Single(await fixture.Gateway.GetModelsAsync(false)).NativeModel);
        Assert.Single(fixture.Adapter.Calls);
    }

    [Fact]
    public async Task UpdatedProfileFreshRefreshPublishesAndCachesNewCatalog()
    {
        using var fixture = new Fixture();
        await fixture.Gateway.GetModelsAsync(true);
        await fixture.Gateway.UpdateAccountAsync(fixture.NewProfile(true));
        Assert.Equal("new-model", Assert.Single(await fixture.Gateway.GetModelsAsync(true)).NativeModel);
        Assert.Equal("new-model", Assert.Single(await fixture.Gateway.GetModelsAsync(false)).NativeModel);
        Assert.Equal(new[] { "old-model", "new-model" }, fixture.Adapter.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "profile-generation-review-" + Guid.NewGuid().ToString("N"));
        public HeldSnapshotStore Store { get; }
        public CatalogAdapter Adapter { get; }
        public LlmGateway Gateway { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(root, "old"));
            Directory.CreateDirectory(Path.Combine(root, "new"));
            Store = new HeldSnapshotStore(JsonAccountStore.InMemory([new AccountProfile { Id = "owned", DisplayName = "Owned old", Provider = ProviderKind.Codex, IsActive = true, ConfigDirectory = Path.Combine(root, "old") }]));
            Adapter = new CatalogAdapter();
            Gateway = new LlmGateway(Store, [Adapter], new GatewayOptions { DiscoverProfiles = false, WorkspaceDirectory = root });
        }
        public AccountProfile NewProfile(bool changeDirectory) => new() { Id = "owned", DisplayName = "Owned new", Provider = ProviderKind.Codex, IsActive = true, ConfigDirectory = Path.Combine(root, changeDirectory ? "new" : "old") };
        public void Dispose() { Store.Release.TrySetResult(); Gateway.Dispose(); Directory.Delete(root, true); }
    }
private sealed class HeldSnapshotStore(JsonAccountStore inner):IAccountStore
{
 int armed;public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public void Arm()=>Interlocked.Exchange(ref armed,1);
 public long RevisionId=>(long?)inner.GetType().GetProperty("RevisionId",BindingFlags.Public|BindingFlags.Instance)?.GetValue(inner)??0;
 public bool IsReadOnly=>false;public string? PersistenceWarning=>null;
 public IReadOnlyList<AccountProfile> GetAll(){var snapshot=inner.GetAll();if(Interlocked.CompareExchange(ref armed,0,1)==1){Entered.TrySetResult();Release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();}return snapshot;}
 public AccountProfile? Find(string id)=>inner.Find(id);
 public Task AddAsync(AccountProfile p,CancellationToken t=default)=>inner.AddAsync(p,t);
 public Task UpdateAsync(AccountProfile p,CancellationToken t=default)=>inner.UpdateAsync(p,t);
 public Task RemoveAsync(string id,CancellationToken t=default)=>inner.RemoveAsync(id,t);
 public Task SelectAsync(string id,CancellationToken t=default)=>inner.SelectAsync(id,t);
}
private sealed class CatalogAdapter:IProviderAdapter
{
 public List<string> Calls{get;}=[];public ProviderKind Provider=>ProviderKind.Codex;public string DisplayName=>"Owned catalog";public string DefaultExecutable=>"owned";
 public ProviderCapabilities Capabilities{get;}=new(false,"owned",MultiAccountSupport.Isolated,"owned",null,null,false,100_000);
 public string? ResolveExecutable(AccountProfile p)=>"owned";public IEnumerable<AccountProfile> DiscoverProfiles()=>[];
 public Task<AccountStatus> GetStatusAsync(AccountProfile p,CancellationToken t)=>Task.FromResult(new AccountStatus(AccountAvailability.Ready,null,"owned",null,DateTimeOffset.UtcNow));
 public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile p,CancellationToken t){var kind=p.DisplayName=="Owned old"?"old-model":"new-model";Calls.Add(kind);return Task.FromResult<IReadOnlyList<NativeModel>>([new(kind,kind,true)]);}
 public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile p,CancellationToken t)=>throw new NotSupportedException();
 public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile p,NativeChatRequest r,CancellationToken t)=>throw new NotSupportedException();
 public Task StartInteractiveLoginAsync(AccountProfile p,CancellationToken t)=>throw new NotSupportedException();
}

}

