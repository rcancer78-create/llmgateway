using LLMGateway.Core;
using System.Text.Json;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class GrokQuotaAvailabilityReviewTests
{
    [Fact]
    public async Task MissingBillingMethodsDoNotProveAuthenticationReadiness()
    {
        using var directory = new TestDirectory();
        var account = await AccountAsync(directory, supported: false);
        var adapter = Adapter(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var quota = await adapter.GetQuotaAsync(account, deadline.Token);

        Assert.False(quota.Supported);
        Assert.Empty(quota.Buckets);
        Assert.Equal(AccountAvailability.Unknown, quota.Availability);
        Assert.Contains("billing", quota.Source);
        VerifyOwnedRpc(account, supported: false);
    }

    [Fact]
    public async Task MissingBillingDoesNotPromotePreviouslyObservedAuthenticationRequired()
    {
        using var directory = new TestDirectory();
        var account = await AccountAsync(directory, supported: false);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([account]), [Adapter(directory)],
            new GatewayOptions { WorkspaceDirectory = directory.Root });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = await gateway.CheckAccountAsync(account.Id, deadline.Token);
        Assert.Equal(AccountAvailability.AuthenticationRequired, observed.Availability);

        var quota = Assert.Single(await gateway.GetQuotasAsync(refresh: true, accountId: account.Id, cancellationToken: deadline.Token));
        var after = Assert.Single(await gateway.GetAccountsAsync(deadline.Token));

        Assert.Equal(AccountAvailability.Unknown, quota.Availability);
        Assert.Equal(AccountAvailability.AuthenticationRequired, after.Availability);
        VerifyOwnedRpc(account, supported: false);
    }

    [Fact]
    public async Task SupportedBillingRetainsReadyAvailabilityAndUsage()
    {
        using var directory = new TestDirectory();
        var account = await AccountAsync(directory, supported: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var quota = await Adapter(directory).GetQuotaAsync(account, deadline.Token);

        Assert.True(quota.Supported);
        Assert.Equal(AccountAvailability.Ready, quota.Availability);
        Assert.Equal("review", quota.Plan);
        Assert.Equal(25d, Assert.Single(quota.Buckets).UsedPercent);
        VerifyOwnedRpc(account, supported: true);
    }

    private static GrokAdapter Adapter(TestDirectory directory) => new(new ExecutableResolver(),
        new GatewayOptions { WorkspaceDirectory = directory.Root }, NullLogger<GrokAdapter>.Instance);

    private static async Task<AccountProfile> AccountAsync(TestDirectory directory, bool supported)
    {
        // Actual owned Node RPC process, reached through the production npm-shim resolver.
        Assert.NotNull(new ExecutableResolver().Resolve("node"));
        var module = Path.Combine(directory.Root, "node_modules", "quota-review");
        Directory.CreateDirectory(module);
        var script = Path.Combine(module, "cli.js");
        await File.WriteAllTextAsync(script, """
            if (process.argv.includes('models')) {
              process.stdout.write('Synthetic fixture has no logged-in account.');
            } else {
              const readline = require('readline');
              const input = readline.createInterface({ input: process.stdin });
              input.on('line', line => {
                const request = JSON.parse(line);
                if (request.id === undefined) return;
                require('fs').appendFileSync(process.env.REVIEW_RPC_CANARY,
                  JSON.stringify({ method: request.method, args: process.argv.slice(2), script: process.argv[1] }) + '\n');
                const response = { jsonrpc: '2.0', id: request.id };
                if (request.method === 'initialize') {
                  response.result = { protocolVersion: 1, agentCapabilities: {} };
                } else if (process.env.REVIEW_BILLING_SUPPORTED === '1') {
                  response.result = { subscription_tier: 'review', config: { creditUsagePercent: 25 } };
                } else {
                  response.error = { code: -32601, message: 'Synthetic billing method not found' };
                }
                process.stdout.write(JSON.stringify(response) + '\n');
              });
            }
            """);
        var shim = directory.GetPath("grok-quota-review.cmd");
        await File.WriteAllTextAsync(shim, "@node \"%~dp0\\node_modules\\quota-review\\cli.js\" %*");
        var target = Assert.IsType<LaunchTarget>(new ExecutableResolver().Resolve(shim));
        Assert.Equal(LaunchKind.Direct, target.Kind);
        Assert.Equal(script, Assert.Single(target.PrefixArguments));
        return new AccountProfile
        {
            Id = "grok-quota-review", Provider = ProviderKind.Grok, Executable = shim,
            WorkingDirectory = directory.Root,
            Environment = new Dictionary<string, string>
            {
                ["REVIEW_BILLING_SUPPORTED"] = supported ? "1" : "0",
                ["REVIEW_RPC_CANARY"] = directory.GetPath("owned-rpc-canary.jsonl")
            }
        };
    }

    private static void VerifyOwnedRpc(AccountProfile account, bool supported)
    {
        var rows = File.ReadAllLines(account.Environment["REVIEW_RPC_CANARY"]);
        var methods = new List<string>();
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row);
            var root = document.RootElement;
            methods.Add(root.GetProperty("method").GetString()!);
            Assert.Equal(Path.Combine(account.WorkingDirectory!, "node_modules", "quota-review", "cli.js"), root.GetProperty("script").GetString());
            var args = root.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("agent", args);
            Assert.Contains("stdio", args);
        }
        Assert.Equal(supported ? new[] { "initialize", "_x.ai/billing" }
            : new[] { "initialize", "_x.ai/billing", "x.ai/billing" }, methods);
    }
}
