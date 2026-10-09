using LLMGateway.Core;
using LLMGateway.Native;
namespace LLMGateway.Tests;
public sealed class NativeEnvironmentShapeReviewTests
{
    [Theory]
    [InlineData("NATIVE_NOTE", "prefix\0INJECTED_NATIVE_VARIABLE=synthetic")]
    [InlineData("BAD\0NAME", "synthetic")]
    [InlineData("BAD=NAME", "synthetic")]
    [InlineData("", "synthetic")]
    public void MalformedEnvironmentIsRefusedBeforeProcessCreation(string name, string value)
    {
        var absent = Path.Combine(Path.GetTempPath(), "owned-absent-" + Guid.NewGuid().ToString("N") + ".exe");
        var launch = new NativeLaunch(new(absent, [], LaunchKind.Direct, absent), [],
            new Dictionary<string, string?> { [name] = value }, Path.GetTempPath());
        var error = Assert.Throws<GatewayException>(() => NativeProcess.Start(launch));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.DoesNotContain("synthetic", error.Message);
    }
}
