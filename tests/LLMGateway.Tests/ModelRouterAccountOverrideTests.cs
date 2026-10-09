using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ModelRouterAccountOverrideTests
{
    private static readonly AccountProfile Source = new()
    {
        Id = "codex-personal", Provider = ProviderKind.Codex, IsActive = true
    };
    private static readonly AccountProfile Target = new()
    {
        Id = "codex-work", Provider = ProviderKind.Codex, DefaultModel = "target-default"
    };
    private static readonly AccountProfile OtherProvider = new()
    {
        Id = "grok-personal", Provider = ProviderKind.Grok
    };

    [Theory]
    [InlineData("gpt-example")]
    [InlineData("vendor/model/version")]
    public void CatalogModelIdCanBeExecutedByAnotherAccountWithoutCorruptingNativeName(string nativeModel)
    {
        var request = new ChatRequest
        {
            Model = ModelRouter.ModelId(Source, nativeModel),
            AccountId = Target.Id
        };
        var route = Resolve(request);
        Assert.Same(Target, route.Account);
        Assert.Equal(nativeModel, route.NativeModel);
        Assert.Equal(ModelRouter.ModelId(Source, nativeModel), request.Model);
    }

    [Theory]
    [InlineData("codex/vendor/model", "vendor/model")]
    [InlineData("codex/unknown-account/vendor/model", "unknown-account/vendor/model")]
    [InlineData("codex/grok-personal/vendor/model", "grok-personal/vendor/model")]
    [InlineData("vendor/model", "vendor/model")]
    [InlineData("codex-work/vendor/model", "codex-work/vendor/model")]
    [InlineData("codex-personal/vendor/model", "codex-personal/vendor/model")]
    public void OverridePreservesNativeNamespacesWithoutAKnownProviderAccountPrefix(string model, string expected)
    {
        var route = Resolve(new ChatRequest { Model = model, AccountId = Target.Id });
        Assert.Same(Target, route.Account);
        Assert.Equal(expected, route.NativeModel);
    }

    [Theory]
    [InlineData("CODEX/CODEX-PERSONAL/vendor/CaseSensitiveModel", "vendor/CaseSensitiveModel")]
    [InlineData("codex/codex-personal", "target-default")]
    [InlineData("codex/codex-personal/auto", "target-default")]
    [InlineData("codex", "target-default")]
    public void OverrideUsesCaseInsensitiveAccountIdsAndTargetDefaults(string model, string expected)
    {
        var route = Resolve(new ChatRequest { Model = model, AccountId = "CODEX-WORK" });
        Assert.Same(Target, route.Account);
        Assert.Equal(expected, route.NativeModel);
    }

    [Fact]
    public void DisabledSourceInModelIdDoesNotDisableAnExplicitEnabledOverride()
    {
        var disabled = new AccountProfile { Id = Source.Id, Provider = Source.Provider, Enabled = false };
        var route = ModelRouter.Resolve(new ChatRequest
        {
            Model = ModelRouter.ModelId(disabled, "vendor/model"), AccountId = Target.Id
        }, [disabled, Target], ProviderKind.Codex, _ => null);
        Assert.Same(Target, route.Account);
        Assert.Equal("vendor/model", route.NativeModel);
    }

    [Fact]
    public void ExplicitOverrideCannotCrossTheProviderEncodedInTheModelId()
    {
        var error = Assert.Throws<GatewayException>(() => Resolve(new ChatRequest
        {
            Model = ModelRouter.ModelId(Source, "vendor/model"), AccountId = OtherProvider.Id
        }));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public void CompositeIdWithoutOverrideStillSelectsItsOriginalAccount()
    {
        var route = Resolve(new ChatRequest { Model = ModelRouter.ModelId(Source, "vendor/model") });
        Assert.Same(Source, route.Account);
        Assert.Equal("vendor/model", route.NativeModel);
    }

    private static RouteResult Resolve(ChatRequest request) => ModelRouter.Resolve(request,
        [Source, Target, OtherProvider], ProviderKind.Codex, _ => null);
}
