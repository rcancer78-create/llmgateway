using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class CodexCatalogPaginationReviewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task CompleteCatalogAtOrBelowPageBoundaryRemainsSuccessful(int pages)
    {
        using var fixture = await Fixture.CreateAsync(pages);
        var models = await fixture.Adapter.ListModelsAsync(fixture.Account, fixture.Token);
        Assert.Equal(pages, models.Count);
        Assert.Equal(pages, fixture.Calls);
        Assert.Equal("owned-page-" + pages, models[^1].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuingOrRepeatedCursorCannotReturnAPartialSuccessfulCatalog(bool repeat)
    {
        using var fixture = await Fixture.CreateAsync(11, repeat);
        var error = await Assert.ThrowsAsync<GatewayException>(() => fixture.Adapter.ListModelsAsync(fixture.Account, fixture.Token));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
        Assert.Equal(10, fixture.Calls);
    }

    [Fact]
    public async Task IncompleteRefreshCannotReplaceHealthyCatalogAndLaterCompleteRefreshRecovers()
    {
        using var fixture = await Fixture.CreateAsync(2);
        using var gateway = fixture.Gateway();
        Assert.Equal(2, (await gateway.GetModelsAsync(refresh: true, cancellationToken: fixture.Token)).Count);
        await fixture.SetPagesAsync(11);
        Assert.Equal(2, (await gateway.GetModelsAsync(refresh: true, cancellationToken: fixture.Token)).Count);
        var afterBad = fixture.Calls;
        Assert.Equal(2, (await gateway.GetModelsAsync(cancellationToken: fixture.Token)).Count);
        Assert.Equal(afterBad, fixture.Calls);
        await fixture.SetPagesAsync(3);
        Assert.Equal(3, (await gateway.GetModelsAsync(refresh: true, cancellationToken: fixture.Token)).Count);
        var afterRecovery = fixture.Calls;
        Assert.Equal(3, (await gateway.GetModelsAsync(cancellationToken: fixture.Token)).Count);
        Assert.Equal(afterRecovery, fixture.Calls);
    }

    [Fact]
    public async Task FailedInitialDiscoveryDoesNotCacheItsPartialModels()
    {
        using var fixture = await Fixture.CreateAsync(11);
        using var gateway = fixture.Gateway();
        var first = await gateway.GetModelsAsync(refresh: true, cancellationToken: fixture.Token);
        Assert.DoesNotContain(first, m => m.NativeModel.StartsWith("owned-page-", StringComparison.Ordinal));
        Assert.Equal(10, fixture.Calls);
        var second = await gateway.GetModelsAsync(cancellationToken: fixture.Token);
        Assert.DoesNotContain(second, m => m.NativeModel.StartsWith("owned-page-", StringComparison.Ordinal));
        Assert.Equal(20, fixture.Calls); // Failure must not publish a fresh successful cache entry.
        await fixture.SetPagesAsync(1);
        Assert.Equal("owned-page-1", Assert.Single(await gateway.GetModelsAsync(refresh: true, cancellationToken: fixture.Token)).NativeModel);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly string _state;
        private readonly string _canary;
        public AccountProfile Account { get; private set; } = null!;
        public CodexAdapter Adapter { get; private set; } = null!;
        public CancellationToken Token => _timeout.Token;
        public int Calls => File.Exists(_canary) ? File.ReadAllLines(_canary).Length : 0;
        private Fixture() { _state = _directory.GetPath("pages.txt"); _canary = _directory.GetPath("owned-rpc.jsonl"); }
        public Task SetPagesAsync(int pages) => File.WriteAllTextAsync(_state, pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public LlmGateway Gateway() => new(JsonAccountStore.InMemory([Account]), [Adapter], new GatewayOptions { WorkspaceDirectory = _directory.Root });
        public static async Task<Fixture> CreateAsync(int pages, bool repeat = false)
        {
            var fixture = new Fixture();
            try
            {
                await fixture.SetPagesAsync(pages);
                var module = Path.Combine(fixture._directory.Root, "node_modules", "owned-pagination");
                Directory.CreateDirectory(module);
                var script = Path.Combine(module, "cli.js");
                await File.WriteAllTextAsync(script, """
                    const fs = require('fs');
                    require('readline').createInterface({input:process.stdin}).on('line', line => {
                      const request = JSON.parse(line);
                      if (request.id === undefined) return;
                      let result = {};
                      if (request.method === 'initialize') result = {userAgent:'owned-pagination/0.0.1'};
                      if (request.method === 'model/list') {
                        const page = Number(request.params?.cursor ?? '1');
                        const pages = Number(fs.readFileSync(process.env.REVIEW_PAGES_FILE, 'utf8'));
                        fs.appendFileSync(process.env.REVIEW_RPC_CANARY, JSON.stringify({page,args:process.argv.slice(2)})+'\n');
                        result = {data:[{model:'owned-page-'+page,displayName:'Owned '+page,isDefault:page===1}],
                          nextCursor:process.env.REVIEW_REPEAT_CURSOR==='1' ? '1' : (page<pages ? String(page+1) : null)};
                      }
                      process.stdout.write(JSON.stringify({id:request.id,result})+'\n');
                    });
                    """);
                var shim = fixture._directory.GetPath("owned-pagination.cmd");
                await File.WriteAllTextAsync(shim, "@node \"%~dp0\\node_modules\\owned-pagination\\cli.js\" %*");
                var target = Assert.IsType<LaunchTarget>(new ExecutableResolver().Resolve(shim));
                Assert.Equal(LaunchKind.Direct, target.Kind);
                Assert.Equal(script, Assert.Single(target.PrefixArguments));
                fixture.Account = new() { Id = "owned-pagination", Provider = ProviderKind.Codex, Executable = shim, IsActive = true, WorkingDirectory = fixture._directory.Root,
                    Environment = new() { ["REVIEW_PAGES_FILE"] = fixture._state, ["REVIEW_RPC_CANARY"] = fixture._canary, ["REVIEW_REPEAT_CURSOR"] = repeat ? "1" : "0" } };
                fixture.Adapter = new(new ExecutableResolver(), new GatewayOptions { WorkspaceDirectory = fixture._directory.Root }, NullLogger<CodexAdapter>.Instance);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public void Dispose() { _timeout.Cancel(); _timeout.Dispose(); _directory.Dispose(); }
    }
}
