using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class ForcedFunctionChoiceReviewTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("auto")]
    [InlineData("required")]
    public void ExplicitFunctionChoiceRequiresExactlyTheNamedFunction(string name)
    {
        var dto = JsonSerializer.Deserialize<OpenAiChatRequest>(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}]," +
            "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"parameters\":{}}}]," +
            "\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"" + name + "\"}}}", GatewayJson.Options)!;
        var request = OpenAiMapper.ToChatRequest(dto);
        Assert.True(ToolCalling.IsEnabled(request));
        Assert.Contains("You must call the function '" + name + "'", ToolCalling.BuildInstructions(request));
        Assert.Throws<GatewayException>(() => ToolCalling.ValidateResult(request, []));
        Assert.Throws<GatewayException>(() => ToolCalling.ValidateResult(request, [new("owned", "other", "{}")]));
        ToolCalling.ValidateResult(request, [new("owned", name, "{}")]);
        var roundTrip = OpenAiMapper.ToChatRequest(OpenAiGatewayClient.ToWire(request, false));
        Assert.True(ToolCalling.IsEnabled(roundTrip));
        Assert.Throws<GatewayException>(() => ToolCalling.ValidateResult(roundTrip, []));
        ToolCalling.ValidateResult(roundTrip, [new("owned", name, "{}")]);
    }

    [Theory]
    [InlineData("none", false, false)]
    [InlineData("auto", true, false)]
    [InlineData("required", true, true)]
    public void BuiltInModesKeepTheirMeaning(string mode, bool enabled, bool requiresCall)
    {
        var request = new ChatRequest { ToolChoice = mode, Tools = [new("owned", null, "{}")], Messages = [ChatMessage.User("owned fixture")] };
        Assert.Equal(enabled, ToolCalling.IsEnabled(request));
        if (requiresCall) Assert.Throws<GatewayException>(() => ToolCalling.ValidateResult(request, []));
        else ToolCalling.ValidateResult(request, []);
        var roundTrip = OpenAiMapper.ToChatRequest(OpenAiGatewayClient.ToWire(request, false));
        Assert.False(roundTrip.ToolChoiceIsFunction);
        Assert.Equal(mode, roundTrip.ToolChoice);
    }
}
