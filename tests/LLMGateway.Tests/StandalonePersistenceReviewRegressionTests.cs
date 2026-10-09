using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Server;

namespace LLMGateway.Tests;

public sealed class StandalonePersistenceReviewRegressionTests
{
    [Theory]
    [InlineData("add", false)]
    [InlineData("update", false)]
    [InlineData("remove", false)]
    [InlineData("select", false)]
    [InlineData("add", true)]
    [InlineData("update", true)]
    [InlineData("remove", true)]
    [InlineData("select", true)]
    public async Task FailedMutationDoesNotPublishOrLaterPersistAnUncommittedSnapshot(string operation, bool ioFailure)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-persistence-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var options = new GatewayOptions { AccountsFile = file, DiscoverProfiles = false };
            var store = JsonAccountStore.Load(options, []);
            await store.AddAsync(Profile("owned-one"));
            await store.AddAsync(Profile("owned-two"));
            var before = Snapshot(store);
            var durableBefore = await File.ReadAllTextAsync(file);
            using var stop = new CancellationTokenSource();
            using var heldTarget = ioFailure ? new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            if (!ioFailure) stop.Cancel();

            var failure = await Record.ExceptionAsync(() => operation switch
            {
                "add" => store.AddAsync(Profile("owned-added"), stop.Token),
                "update" => store.UpdateAsync(Profile("owned-one", "Uncommitted change"), stop.Token),
                "remove" => store.RemoveAsync("owned-one", stop.Token),
                "select" => store.SelectAsync("owned-two", stop.Token),
                _ => throw new InvalidOperationException("Unknown owned fixture operation.")
            });
            Assert.NotNull(failure);
            heldTarget?.Dispose();
            Assert.Equal(durableBefore, await File.ReadAllTextAsync(file));
            var observedAfterFailure = Snapshot(store);

            // A subsequent successful save must not accidentally commit the rejected mutation.
            var unchanged = store.Find("codex-default")!;
            await store.UpdateAsync(unchanged);
            var reloaded = JsonAccountStore.Load(options, []);
            Assert.Equal(before, observedAfterFailure);
            Assert.Equal(before, Snapshot(reloaded));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("Bearer synthetic-credential", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmaXh0dXJlIn0.c3ludGhldGlj", true)]
    [InlineData("ordinary-setting", false)]
    public async Task AccountApiRefusesCredentialShapesUnderBenignEnvironmentNames(string value, bool reject)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-environment-");
        try
        {
            var store = JsonAccountStore.InMemory([Profile("owned-seed")], Path.Combine(root.FullName, "accounts.json"));
            var adapter = new FakeAdapter();
            using var gateway = new LlmGateway(store, [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "owned-fixture" });
            using var http = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owned-fixture");
            var body = JsonSerializer.Serialize(new
            {
                id = "owned-new", display_name = "Owned account", provider = "codex",
                environment = new Dictionary<string, string> { ["NOTE"] = value }
            });
            using var response = await http.PostAsync("/v1/gateway/accounts", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(reject ? HttpStatusCode.BadRequest : HttpStatusCode.Created, response.StatusCode);
            if (reject)
            {
                Assert.Null(store.Find("owned-new"));
                Assert.False(File.Exists(store.FilePath));
                var publicAccounts = await http.GetStringAsync("/v1/gateway/accounts");
                Assert.DoesNotContain(value, publicAccounts);
            }
            else
            {
                Assert.Equal(value, store.Find("owned-new")!.Environment["NOTE"]);
                Assert.Contains(value, await File.ReadAllTextAsync(store.FilePath));
            }
            Assert.Empty(adapter.Calls);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task CompleteLegacyProviderSetIsDurablyMigratedWithoutRelyingOnDefaultAdditions()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-migration-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var providers = Enum.GetValues<ProviderKind>().Where(provider => provider != ProviderKind.Unknown).ToArray();
            var legacy = providers.Select(provider => new
            { id = provider.ToString().ToLowerInvariant() + "-owned", provider = provider.ToString(), isActive = true });
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(legacy));
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.Equal(providers.Length, store.GetAll().Count);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal(JsonValueKind.Object, persisted.RootElement.ValueKind);
            Assert.Equal(2, persisted.RootElement.GetProperty("version").GetInt32());
        }
        finally { root.Delete(true); }
    }

    private static AccountProfile Profile(string id, string? name = null) => new()
    { Id = id, DisplayName = name ?? id, Provider = ProviderKind.Codex };

    private static string Snapshot(JsonAccountStore store) =>
        JsonSerializer.Serialize(store.GetAll().OrderBy(account => account.Id), GatewayJson.Options);
}
