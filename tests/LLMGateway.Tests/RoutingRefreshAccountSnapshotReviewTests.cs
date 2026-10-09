using System.Runtime.CompilerServices;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class RoutingRefreshAccountSnapshotReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogAwaitCannotRouteAnUnadmittedRequestThroughReplacedProfile(bool removeAndReadd)
    {
        var directory=Directory.CreateTempSubdirectory("llmgw-owned-routing-refresh-");
        var adapter=new Adapter();
        Task<ChatResult>? completion=null;
        try
        {
            using var gateway=new LlmGateway(JsonAccountStore.InMemory([Profile("Owned old","owned-old")]),[adapter],
                new GatewayOptions { WorkspaceDirectory=directory.FullName });
            completion=gateway.CompleteAsync(new ChatRequest { Model="owned-model",Messages=[ChatMessage.User("Owned routing fixture")] });
            try
            {
                await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(adapter.Chats);
                if(removeAndReadd)
                {
                    await gateway.RemoveAccountAsync("owned");
                    await gateway.AddAccountAsync(Profile("Owned replacement","owned-new"));
                }
                else await gateway.UpdateAccountAsync(Profile("Owned replacement","owned-new"));
                await gateway.GetModelsAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
                adapter.Release.TrySetResult();
                Assert.Equal("owned answer",(await completion.WaitAsync(TimeSpan.FromSeconds(5))).Content);
                var routed=Assert.Single(adapter.Chats);
                Assert.Equal("Owned replacement",routed.DisplayName);
                Assert.Equal("owned-new",routed.Executable);
            }
            finally
            {
                adapter.Release.TrySetResult();
                await Record.ExceptionAsync(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally { directory.Delete(true); }
    }
    private static AccountProfile Profile(string name,string executable) => new()
    { Id="owned",Provider=ProviderKind.Codex,IsActive=true,DisplayName=name,Executable=executable };
    private sealed class Adapter : IProviderAdapter
    {
        private int _catalogCalls;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AccountProfile> Chats { get; }=[];
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned scripted";
        public string DefaultExecutable => "owned-scripted";
        public ProviderCapabilities Capabilities { get; }=new(true,"owned",MultiAccountSupport.Isolated,"owned",null,null,true,100_000);
        public string? ResolveExecutable(AccountProfile profile) => profile.Executable;
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile,CancellationToken token) => Task.FromResult(new AccountStatus(AccountAvailability.Ready,null,null,null,DateTimeOffset.UtcNow));
        public async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile,CancellationToken token)
        {
            if(Interlocked.Increment(ref _catalogCalls)==1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return [new("owned-model","Owned",true)];
        }
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile,CancellationToken token) => Task.FromResult(new QuotaSnapshot(profile.Id,profile.Provider,DateTimeOffset.UtcNow,AccountAvailability.Ready,true,null,[],"owned",null));
        public Task StartInteractiveLoginAsync(AccountProfile profile,CancellationToken token) => Task.CompletedTask;
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile,NativeChatRequest request,[EnumeratorCancellation] CancellationToken token)
        {
            Chats.Add(profile.Clone());
            await Task.Yield();
            yield return NativeChatEvent.Delta("owned answer");
        }
    }
}
