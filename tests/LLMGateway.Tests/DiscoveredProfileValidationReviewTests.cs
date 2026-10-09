using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class DiscoveredProfileValidationReviewTests
{
    [Theory]
    [InlineData("unsafe-arguments")]
    [InlineData("unknown-auth")]
    [InlineData("null-environment")]
    [InlineData("null-arguments")]
    [InlineData("null-profile")]
    public async Task InvalidDiscoveredProfileCannotBePublished(string shape)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-discovery-validation-");
        try
        {
            var path = Path.Combine(root.FullName, "accounts.json");
            const string original = """{"version":2,"accounts":[{"id":"existing","provider":"codex"}]}""";
            await File.WriteAllTextAsync(path, original);
            var bytes = await File.ReadAllBytesAsync(path);
            AccountProfile? profile = new() { Id = "discovered", Provider = ProviderKind.Codex };
            switch (shape)
            {
                case "unsafe-arguments": profile.ExtraArguments = ["--yolo"]; break;
                case "unknown-auth": profile.AuthMode = (AccountAuthMode)99; break;
                case "null-environment": profile.Environment = null!; break;
                case "null-arguments": profile.ExtraArguments = null!; break;
                case "null-profile": profile = null; break;
            }
            var error = Assert.Throws<GatewayException>(() => JsonAccountStore.Load(
                new GatewayOptions { AccountsFile = path, DiscoverProfiles = true }, [new DiscoveryAdapter(profile)]));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(root.FullName, "*.tmp"));
        }
        finally { root.Delete(true); }
    }

    private sealed class DiscoveryAdapter(AccountProfile? profile) : IProviderAdapter
    {
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "discovery fixture";
        public string DefaultExecutable => "unused";
        public ProviderCapabilities Capabilities => new(true, "fixture", MultiAccountSupport.Isolated, "fixture", null, null, true, 100000);
        public string? ResolveExecutable(AccountProfile account) => null;
        public IEnumerable<AccountProfile> DiscoverProfiles() => [profile!];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
