using LLMGateway.Native;

namespace LLMGateway.Tests;

[Collection("Process PATH isolation")]
public sealed class ExecutablePathPrecedenceReviewTests
{
    [Theory]
    [InlineData("shim-first")]
    [InlineData("exe-first")]
    [InlineData("explicit-shim")]
    [InlineData("same-directory")]
    public void WindowsPathOrderPreservesTheEarlierClientAndUnwrapsItsNpmLauncher(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("owned-path-precedence-");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root.FullName, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root.FullName, "second")).FullName;
            var script = Path.Combine(first, "node_modules", "owned", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// Owned fixture; never executed.\n");
            var shim = Path.Combine(first, "owned-client.cmd");
            var node = Path.Combine(first, "node.exe");
            var direct = Path.Combine(second, "owned-client.exe");
            File.WriteAllText(shim, "@node \"%dp0%\\node_modules\\owned\\cli.js\" %*\n");
            File.WriteAllBytes(node, []);
            File.WriteAllBytes(direct, []);

            var path = scenario is "exe-first" or "explicit-shim"
                ? second + ";" + first : first + ";" + second;
            var requested = scenario == "explicit-shim" ? shim : "owned-client";
            var expectedOrigin = scenario == "exe-first" ? direct : shim;
            var expectedExecutable = scenario == "exe-first" ? direct : node;
            string[] expectedArguments = scenario == "exe-first" ? [] : [script];
            if (scenario == "same-directory")
            {
                expectedOrigin = expectedExecutable = Path.Combine(first, "owned-client.exe");
                expectedArguments = [];
                File.WriteAllBytes(expectedExecutable, []);
            }
            Environment.SetEnvironmentVariable("PATH", path);
            var target = Assert.IsType<LaunchTarget>(new ExecutableResolver().Resolve(requested));
            Assert.Equal(expectedOrigin, target.ResolvedFrom);
            Assert.Equal(expectedExecutable, target.FileName);
            Assert.Equal(LaunchKind.Direct, target.Kind);
            Assert.Equal(expectedArguments, target.PrefixArguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            root.Delete(true);
        }
    }
}

[CollectionDefinition("Process PATH isolation", DisableParallelization = true)]
public sealed class ProcessPathIsolationCollection
{
}
