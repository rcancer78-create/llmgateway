using LLMGateway.Core;
using LLMGateway.Native;
using Microsoft.Extensions.Logging.Abstractions;
namespace LLMGateway.Tests;
public sealed class ConfigDirectoryCredentialExpansionReviewTests
{
    [Theory]
    [InlineData("configured", false, true)]
    [InlineData("configured", true, true)]
    [InlineData("secret-name", false, true)]
    [InlineData("secret-name", true, true)]
    [InlineData("secret-value", false, true)]
    [InlineData("secret-value", true, true)]
    [InlineData("public", false, false)]
    [InlineData("public", true, false)]
    [InlineData("literal", false, false)]
    [InlineData("literal", true, false)]
    [InlineData("unresolved", false, false)]
    [InlineData("unresolved", true, false)]
    public void ExpansionCannotPublishCredentialsAndPreservesOrdinaryPathSemantics(string mode, bool login, bool blocked)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-config-expansion-");
        var name = "LLMGW_" + Guid.NewGuid().ToString("N") + (mode == "secret-name" ? "_API_KEY" : "_PATH");
        var value = mode == "secret-value" ? "sk-owned-config-fixture-not-a-key" : "owned-public-target";
        // Unique synthetic variable only: no real credential is read or overwritten.
        if (mode != "unresolved") Environment.SetEnvironmentVariable(name, value);
        try
        {
            var options = new GatewayOptions();
            if (mode == "configured")
            {
                var property = typeof(GatewayOptions).GetProperty("CredentialVariableNames")!;
                property.SetValue(options, property.PropertyType == typeof(Func<IEnumerable<string>>) ? (object)new Func<IEnumerable<string>>(() => [name]) : new[] { name });
            }
            var profile = new AccountProfile { Id = "owned", Provider = ProviderKind.Codex,
                ConfigDirectory = mode == "literal" ? Path.Combine(root.FullName, "literal") : Path.Combine(root.FullName, "%" + name + "%", "profile") };
            if (mode == "public") profile.Environment[name] = Path.Combine(root.FullName, "child-override");
            var expected = Path.GetFullPath(Environment.ExpandEnvironmentVariables(profile.ConfigDirectory));
            var adapter = new Adapter(options);
            if (blocked)
            {
                var error = Assert.Throws<GatewayException>(() => adapter.Read(profile, login));
                Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
                Assert.DoesNotContain(value, error.Message);
                Assert.False(Directory.Exists(expected));
            }
            else
            {
                Assert.Equal(expected, adapter.Read(profile, login)["CODEX_HOME"]);
                Assert.True(Directory.Exists(expected));
            }
        }
        finally { Environment.SetEnvironmentVariable(name, null); root.Delete(true); }
    }
    private sealed class Adapter(GatewayOptions options) : NativeAdapterBase(new ExecutableResolver(), options, NullLogger.Instance)
    {
        public IReadOnlyDictionary<string, string?> Read(AccountProfile account, bool login) => BuildEnvironment(account, login);
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Owned config fixture";
        public override string DefaultExecutable => "owned-no-client";
        public override ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", "CODEX_HOME", "CODEX_API_KEY", false, 1000);
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => throw new NotSupportedException();
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
    }
}
