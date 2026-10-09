using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class DefaultProfileIdCollisionReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReassignedDefaultIdSurvivesReloadWithoutLosingAccount(bool removeAndAdd, bool uppercase)
    {
        using var fixture = new Fixture();
        var profile = fixture.Store.Find("grok-default")!;
        profile.Provider = ProviderKind.Claude;
        profile.DisplayName = "Owned reassigned account";
        if (removeAndAdd)
        {
            await fixture.Store.RemoveAsync("grok-default");
            if (uppercase) profile.Id = "GROK-DEFAULT";
            await fixture.Store.AddAsync(profile);
        }
        else await fixture.Store.UpdateAsync(profile);
        fixture.VerifyPreservedCollisionAndStableDefault(profile.Id);
    }

    [Fact]
    public async Task ExistingSuffixIsPreservedWhenFreshDefaultNeedsAnotherId()
    {
        using var fixture = new Fixture();
        var profile = fixture.Store.Find("grok-default")!;
        profile.Provider = ProviderKind.Claude;
        await fixture.Store.UpdateAsync(profile);
        await fixture.Store.AddAsync(new AccountProfile { Id = "grok-default-2", DisplayName = "Owned occupied suffix", Provider = ProviderKind.Claude });
        var reloaded = fixture.VerifyPreservedCollisionAndStableDefault("grok-default");
        Assert.Equal("Owned occupied suffix", reloaded.Find("grok-default-2")!.DisplayName);
        Assert.NotEqual("grok-default-2", Assert.Single(reloaded.GetAll(), p => p.Provider == ProviderKind.Grok).Id);
    }

    [Fact]
    public async Task OrdinaryDefaultUpdateKeepsItsIdAndReloads()
    {
        using var fixture = new Fixture();
        var profile = fixture.Store.Find("grok-default")!;
        profile.DisplayName = "Owned ordinary update";
        await fixture.Store.UpdateAsync(profile);
        var reloaded = fixture.Reload();
        Assert.Equal("Owned ordinary update", reloaded.Find("grok-default")!.DisplayName);
        Assert.Equal(ProviderKind.Grok, reloaded.Find("grok-default")!.Provider);
        Assert.Equal(fixture.Store.GetAll().Count, reloaded.GetAll().Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "default-id-review-" + Guid.NewGuid().ToString("N"));
        private readonly GatewayOptions options;
        public JsonAccountStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            options = new GatewayOptions { AccountsFile = Path.Combine(root, "accounts.json"), WorkspaceDirectory = root, DiscoverProfiles = false };
            Store = JsonAccountStore.Load(options, []);
        }
        public JsonAccountStore Reload() => JsonAccountStore.Load(options, []);
        public JsonAccountStore VerifyPreservedCollisionAndStableDefault(string originalId)
        {
            var original = Store.Find(originalId)!;
            var reloaded = Reload();
            Assert.Equal(ProviderKind.Claude, reloaded.Find(originalId)!.Provider);
            Assert.Equal(original.DisplayName, reloaded.Find(originalId)!.DisplayName);
            var generated = Assert.Single(reloaded.GetAll(), p => p.Provider == ProviderKind.Grok);
            Assert.False(generated.Id.Equals(originalId, StringComparison.OrdinalIgnoreCase));
            Assert.True(generated.IsActive);
            Assert.Equal(reloaded.GetAll().Count, reloaded.GetAll().Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var bytes = File.ReadAllBytes(options.AccountsFile!);
            var again = Reload();
            Assert.Equal(generated.Id, Assert.Single(again.GetAll(), p => p.Provider == ProviderKind.Grok).Id);
            Assert.Equal(bytes, File.ReadAllBytes(options.AccountsFile!));
            return reloaded;
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
