using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class NativeStdinDeliveryReviewTests
{
    [Theory]
    [InlineData("close", GatewayErrorKind.Upstream)]
    [InlineData("read", null)]
    [InlineData("auth", GatewayErrorKind.AuthenticationRequired)]
    [InlineData("rate", GatewayErrorKind.RateLimited)]
    [InlineData("hold", null)]
    public async Task OnlyFullyWrittenInputCanPublishZeroExitCompletion(string mode, GatewayErrorKind? expected)
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("owned-stdin.mjs");
        await File.WriteAllTextAsync(script, """
            import fs from 'node:fs';
            import crypto from 'node:crypto';
            const mode = process.argv[2], receipt = process.argv[3];
            if (mode === 'read') {
              const parts = [];
              for await (const chunk of process.stdin) parts.push(chunk);
              const input = Buffer.concat(parts);
              fs.writeFileSync(receipt, JSON.stringify({bytes:input.length,sha256:crypto.createHash('sha256').update(input).digest('hex')}));
            } else if (mode !== 'hold') {
              fs.closeSync(0);
              fs.writeFileSync(receipt, JSON.stringify({bytes:0}));
            }
            fs.writeFileSync(receipt + '.ready', String(process.pid));
            const until = Date.now() + 20000;
            while (!fs.existsSync(receipt + '.release')) {
              if (Date.now() >= until) process.exit(8);
              await new Promise(resolve => setTimeout(resolve, 10));
            }
            console.log(JSON.stringify({type:'item.completed',item:{type:'agent_message',text:'owned answer'}}));
            console.log(JSON.stringify({type:'turn.completed',usage:{input_tokens:1,output_tokens:1}}));
            if (mode === 'auth') { console.error('Authentication required. Please login.'); process.exitCode = 1; }
            if (mode === 'rate') { console.error("You've hit your usage limit. Try again in 2h."); process.exitCode = 1; }
            """);
        var target = new ExecutableResolver().Resolve("node");
        Assert.NotNull(target);
        var input = new string('x', 512 * 1024);
        var receipt = directory.GetPath("receipt.json");
        var launch = new NativeLaunch(target, [script, mode, receipt], new Dictionary<string, string?>(), directory.Root, input);
        var adapter = new OwnedAdapter(launch);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory(
            [new() { Id = "owned", Provider = ProviderKind.Codex, Executable = target.FileName, IsActive = true }]),
            [adapter], new GatewayOptions { DiscoverProfiles = false, WorkspaceDirectory = directory.Root });
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var updates = new List<ChatUpdate>();
        var run = Record.ExceptionAsync(async () =>
        {
            await foreach (var update in gateway.StreamAsync(new ChatRequest
                { Model = "owned-model", AccountId = "owned", Messages = [ChatMessage.User("owned request")] }, caller.Token))
                updates.Add(update);
        });
        Process? child = null;
        try
        {
            while (!File.Exists(receipt + ".ready"))
            {
                Assert.False(run.IsCompleted, "Owned fixture must reach its native input boundary before finishing.");
                await Task.Delay(10, caller.Token);
            }
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(receipt + ".ready", caller.Token)));
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            if (mode == "hold") caller.Cancel();
            else await File.WriteAllTextAsync(receipt + ".release", "owned release", caller.Token);
            var failure = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited, "Returning must join physical cleanup of this owned process.");
            if (mode == "hold")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
                Assert.DoesNotContain(updates, u => u.Kind == ChatUpdateKind.Completed);
            }
            else if (expected is { } kind)
            {
                Assert.Equal(kind, Assert.IsType<GatewayException>(failure).Kind);
                Assert.DoesNotContain(updates, u => u.Kind == ChatUpdateKind.Completed);
            }
            else
            {
                Assert.Null(failure);
                Assert.Equal("owned answer", Assert.Single(updates, u => u.Kind == ChatUpdateKind.Completed).Result!.Content);
                using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                Assert.Equal(Encoding.UTF8.GetByteCount(input), actual.RootElement.GetProperty("bytes").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant(),
                    actual.RootElement.GetProperty("sha256").GetString());
            }
        }
        finally
        {
            caller.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                child.Dispose();
            }
        }
    }

    private sealed class OwnedAdapter(NativeLaunch launch) : NativeAdapterBase(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance)
    {
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Owned stdin regression";
        public override string DefaultExecutable => launch.Target.FileName;
        public override ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, false, 1_000_000);
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("owned-model", "Owned model", true)]);
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult(QuotaSnapshot.Unsupported(account, AccountAvailability.Ready, null, "owned", "owned"));
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) =>
            StreamAsync(launch, new CodexAdapter.ExecParser(), token);
    }
}
