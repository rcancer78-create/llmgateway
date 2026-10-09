using System.Runtime.CompilerServices;
using LLMGateway.Core;
using Xunit;
namespace LLMGateway.Tests;
public sealed class ConcurrentCatalogMissReviewTests
{
    [Fact]
    public async Task ConcurrentDistinctUnknownModelsShareOneRefresh()
    {
        using var fixture = new Fixture();
        await fixture.Gateway.GetModelsAsync();
        fixture.Adapter.HoldRefresh = true;
        var requests = Enumerable.Range(0, 6).Select(i => fixture.Gateway.CompleteAsync(Request("missing-" + i))).ToArray();
        try
        {
            await fixture.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Adapter.Release.TrySetResult();
            foreach (var request in requests)
                Assert.Equal(GatewayErrorKind.ModelNotFound, (await Assert.ThrowsAsync<GatewayException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)))).Kind);
            Assert.Equal(2, fixture.Adapter.CatalogCalls);
            Assert.Equal(0, fixture.Adapter.ChatCalls);
        }
        finally { fixture.Adapter.Release.TrySetResult(); await Task.WhenAll(requests.Select(r => Record.ExceptionAsync(() => r))); }
    }

    [Fact]
    public async Task FirstMissDiscoversNewModelInsideLiveCatalog()
    {
        using var fixture = new Fixture();
        await fixture.Gateway.GetModelsAsync();
        fixture.Adapter.Models = [new("new-model", "New")];
        Assert.Equal("new-model", (await fixture.Gateway.CompleteAsync(Request("new-model"))).NativeModel);
        Assert.Equal(2, fixture.Adapter.CatalogCalls);
        Assert.Equal(1, fixture.Adapter.ChatCalls);
    }

    [Fact]
    public async Task ExplicitRefreshAfterMissDiscoversNewModel()
    {
        using var fixture = new Fixture();
        await fixture.Gateway.GetModelsAsync();
        await Assert.ThrowsAsync<GatewayException>(() => fixture.Gateway.CompleteAsync(Request("missing")));
        fixture.Adapter.Models = [new("new-model", "New")];
        Assert.Equal("new-model", Assert.Single(await fixture.Gateway.GetModelsAsync(true)).NativeModel);
        Assert.Equal("new-model", (await fixture.Gateway.CompleteAsync(Request("new-model"))).NativeModel);
        Assert.Equal(3, fixture.Adapter.CatalogCalls);
    }

    [Fact]
    public async Task CancellingFirstWaiterDoesNotCancelSharedDiscovery()
    {
        using var fixture = new Fixture();
        await fixture.Gateway.GetModelsAsync();
        fixture.Adapter.Models = [new("new-model", "New")];
        fixture.Adapter.HoldRefresh = true;
        using var cancel = new CancellationTokenSource();
        var first = fixture.Gateway.CompleteAsync(Request("missing"), cancel.Token);
        await fixture.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Gateway.CompleteAsync(Request("new-model"));
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            fixture.Adapter.Release.TrySetResult();
            Assert.Equal("new-model", (await second.WaitAsync(TimeSpan.FromSeconds(5))).NativeModel);
            Assert.Equal(2, fixture.Adapter.CatalogCalls);
            Assert.Equal(1, fixture.Adapter.ChatCalls);
        }
        finally { fixture.Adapter.Release.TrySetResult(); await Record.ExceptionAsync(() => first); await Record.ExceptionAsync(() => second); }
    }

    private static ChatRequest Request(string model) => new() { Model = model, Messages = [ChatMessage.User("public fixture")] };
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("llmgw-catalog-miss-");
        public DeferredAdapter Adapter { get; } = new();
        public LlmGateway Gateway { get; }
        public Fixture() => Gateway = new LlmGateway(JsonAccountStore.InMemory([new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]), [Adapter], new GatewayOptions { WorkspaceDirectory = _root.FullName });
        public void Dispose() { Adapter.Release.TrySetResult(); Gateway.Dispose(); _root.Delete(true); }
    }
    private sealed class DeferredAdapter : IProviderAdapter
    {
        private int _catalogCalls, _chatCalls;
        public int CatalogCalls => Volatile.Read(ref _catalogCalls);
        public int ChatCalls => Volatile.Read(ref _chatCalls);
        public bool HoldRefresh { get; set; }
        public IReadOnlyList<NativeModel> Models { get; set; } = [new("known-model", "Known")];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, false, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token)
        {
            if (Interlocked.Increment(ref _catalogCalls) > 1 && HoldRefresh) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return Models;
        }
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => Task.CompletedTask;
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Interlocked.Increment(ref _chatCalls); await Task.Yield(); yield return NativeChatEvent.Delta("owned answer"); }
    }
}
