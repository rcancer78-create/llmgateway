using System.Net;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.Client;
namespace LLMGateway.Tests;
public sealed class RemoteStreamToolShapeReviewTests
{
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"call1\",\"function\":{\"name\":\"known\",\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":-1,\"id\":\"call1\",\"function\":{\"name\":\"known\",\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":0,\"function\":{\"name\":\"known\",\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":0,\"id\":\" \",\"function\":{\"name\":\"known\",\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":0,\"id\":\"call1\",\"function\":{\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":0,\"id\":\"call1\",\"function\":{\"name\":\" \",\"arguments\":\"{}\"}}]")]
    [InlineData("[{\"index\":0,\"id\":\"call1\",\"function\":{\"name\":\"known\"}}]")]
    [InlineData("[{\"index\":0,\"id\":\"call1\",\"function\":{\"name\":\"known\",\"arguments\":null}}]")]
    [InlineData("[{\"index\":0,\"id\":\"call1\",\"function\":null}]")]
    [InlineData("[]")]
    public async Task DoneAndFinishCannotTurnMalformedToolsIntoCompletedReply(string tools)
    {
        await Refused(Frame("{\"tool_calls\":"+tools+"}","tool_calls")+Done);
    }
    [Fact]
    public async Task DuplicateCallIdsCannotProduceAmbiguousToolResults()
    {
        var tools=JsonSerializer.Serialize(new[] { Call(0,"same","{}"),Call(1,"same","{}") });
        await Refused(Frame("{\"tool_calls\":"+tools+"}","tool_calls")+Done);
    }
    [Theory]
    [InlineData("", "{}", "{}")]
    [InlineData("{", "\"value\":1}", "{\"value\":1}")]
    [InlineData("", "", "")]
    public async Task ValidFragmentsAndExplicitEmptyArgumentsRetainActualFields(string first,string tail,string expected)
    {
        var begin=JsonSerializer.Serialize(new { tool_calls=new[] { Call(0,"call1",first) } });
        var end=JsonSerializer.Serialize(new { tool_calls=new[] { new { index=0,function=new { arguments=tail } } } });
        var result=await Completed(Frame(begin)+Frame(end,"tool_calls")+Done);
        var call=Assert.Single(result.ToolCalls);
        Assert.Equal("call1",call.Id);Assert.Equal("known",call.Name);Assert.Equal(expected,call.ArgumentsJson);
    }
    [Fact]
    public async Task ArgumentFragmentsCanPrecedeIdentityMetadataWithoutInventingFields()
    {
        var first=Frame("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{}\"}}]}");
        var last=Frame("{\"tool_calls\":[{\"index\":0,\"id\":\"late\",\"function\":{\"name\":\"known\"}}]}","tool_calls");
        var call=Assert.Single((await Completed(first+last+Done)).ToolCalls);
        Assert.Equal("late",call.Id);Assert.Equal("known",call.Name);Assert.Equal("{}",call.ArgumentsJson);
    }
    [Fact]
    public async Task ToolCountBudgetAppliesAcrossSeparateFrames()
    {
        var frames=string.Concat(Enumerable.Range(0,129).Select(i=>Frame(JsonSerializer.Serialize(new { tool_calls=new[] { Call(i,"call"+i,"{}") } }))));
        await Refused(frames+Frame("{}","tool_calls")+Done);
    }
    [Fact]
    public async Task ArgumentBudgetAppliesAcrossFragmentsOfOneTool()
    {
        var first=Frame(JsonSerializer.Serialize(new { tool_calls=new[] { Call(0,"call1",new string('x',512*1024)) } }));
        var tail=Frame(JsonSerializer.Serialize(new { tool_calls=new[] { new { index=0,function=new { arguments=new string('x',512*1024+1) } } } }),"tool_calls");
        await Refused(first+tail+Done);
    }
    private static object Call(int index,string id,string arguments) => new { index,id,function=new { name="known",arguments } };
    private const string Done="data: [DONE]\n\n";
    private static string Frame(string delta,string? finish=null) => "data: {\"id\":\"owned\",\"model\":\"owned-model\",\"choices\":[{\"index\":0,\"delta\":"+delta+",\"finish_reason\":"+JsonSerializer.Serialize(finish)+"}]}\n\n";
    private static ChatRequest Request() => new() { Model="owned-model",Messages=[ChatMessage.User("owned shape fixture")] };
    private static async Task Refused(string frames)
    {
        using var body=new ObservedBody(Encoding.UTF8.GetBytes(frames));
        using var http=new HttpClient(new Handler(body)) { BaseAddress=new Uri("https://owned-gateway.invalid/") };
        using var gateway=new OpenAiGatewayClient(http);var updates=new List<ChatUpdate>();
        var failure=await Record.ExceptionAsync(async()=> { await foreach(var update in gateway.StreamAsync(Request()))updates.Add(update); });
        var error=Assert.IsType<GatewayException>(failure);Assert.Equal(GatewayErrorKind.Upstream,error.Kind);
        Assert.DoesNotContain(updates,u=>u.Kind==ChatUpdateKind.Completed);Assert.True(body.Disposed);
    }
    private static async Task<ChatResult> Completed(string frames)
    {
        using var http=new HttpClient(new Handler(new MemoryStream(Encoding.UTF8.GetBytes(frames)))) { BaseAddress=new Uri("https://owned-gateway.invalid/") };
        using var gateway=new OpenAiGatewayClient(http);var updates=new List<ChatUpdate>();
        await foreach(var update in gateway.StreamAsync(Request()))updates.Add(update);
        return Assert.Single(updates,u=>u.Kind==ChatUpdateKind.Completed).Result!;
    }
    private sealed class Handler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StreamContent(stream) });
    }
    private sealed class ObservedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed=true;base.Dispose(disposing); }
    }
}
