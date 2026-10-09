using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LLMGateway.Native;

public enum LaunchKind
{
    /// <summary>A real executable; arguments are passed with standard Windows escaping.</summary>
    Direct,

    /// <summary>A wrapper that could not be unwrapped; execution is refused before process creation.</summary>
    Script
}

public sealed record LaunchTarget(string FileName, IReadOnlyList<string> PrefixArguments, LaunchKind Kind, string ResolvedFrom);

/// <summary>
/// Finds native clients on PATH and unwraps the common Windows launchers (npm shims, the cursor-agent
/// PowerShell launcher) into a direct node.exe invocation, so prompts never pass through cmd.exe or PowerShell parsing.
/// </summary>
public sealed partial class ExecutableResolver
{
    private static readonly string[] Extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ".ps1", ".com"] : [string.Empty];
    private readonly ConcurrentDictionary<string, (DateTime At, LaunchTarget? Target)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LaunchTarget? Resolve(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (_cache.TryGetValue(executable, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2)) return cached.Target;
        var target = ResolveCore(executable.Trim());
        _cache[executable] = (DateTime.UtcNow, target);
        return target;
    }

    public void Invalidate() => _cache.Clear();

    private static LaunchTarget? ResolveCore(string executable)
    {
        var path = FindFile(executable);
        if (path is null) return null;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cmd" or ".bat" or ".ps1")
        {
            return TryUnwrapCursorLauncher(path)
                ?? TryUnwrapNpmShim(path)
                ?? (extension == ".ps1"
                    ? new LaunchTarget(PowerShellPath(), ["-NoProfile", "-NonInteractive", "-File", path], LaunchKind.Script, path)
                    : new LaunchTarget(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/s", "/c", path], LaunchKind.Script, path));
        }
        return new LaunchTarget(path, [], LaunchKind.Direct, path);
    }

    private static string? FindFile(string executable)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(executable);
            if (File.Exists(full)) return full;
            return Extensions.Select(e => full + e).FirstOrDefault(File.Exists);
        }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // PATH directory order selects the client installation; extension
        // precedence applies within that directory, including npm launchers.
        foreach (var directory in directories)
        {
            foreach (var extension in Extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory, executable.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? executable : executable + extension); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>cursor-agent.ps1 runs versions/&lt;latest&gt;/node.exe index.js.</summary>
    private static LaunchTarget? TryUnwrapCursorLauncher(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var local = Path.Combine(directory, "node.exe");
        var localIndex = Path.Combine(directory, "index.js");
        if (File.Exists(local) && File.Exists(localIndex)) return new LaunchTarget(local, [localIndex], LaunchKind.Direct, path);
        var versions = Path.Combine(directory, "versions");
        if (!Directory.Exists(versions)) return null;
        var latest = Directory.EnumerateDirectories(versions)
            .Select(d => (Path: d, Key: VersionKey(Path.GetFileName(d))))
            .Where(v => v.Key is not null && File.Exists(Path.Combine(v.Path, "node.exe")) && File.Exists(Path.Combine(v.Path, "index.js")))
            .OrderByDescending(v => v.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return latest.Path is null ? null
            : new LaunchTarget(Path.Combine(latest.Path, "node.exe"), [Path.Combine(latest.Path, "index.js")], LaunchKind.Direct, path);
    }

    /// <summary>npm shims: "%dp0%\node_modules\pkg\bin\cli.js" or "$basedir/node_modules/pkg/bin/cli.js".</summary>
    private static LaunchTarget? TryUnwrapNpmShim(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return null; }
        var match = NpmScriptPattern().Match(text);
        if (!match.Success) return null;
        var directory = Path.GetDirectoryName(path)!;
        var script = Path.GetFullPath(Path.Combine(directory, match.Groups["script"].Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!File.Exists(script)) return null;
        var node = Path.Combine(directory, "node.exe");
        if (!File.Exists(node)) node = FindFile("node") ?? string.Empty;
        return node.Length == 0 || !node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows()
            ? null
            : new LaunchTarget(node, [script], LaunchKind.Direct, path);
    }

    private static string? VersionKey(string name)
    {
        var match = VersionPattern().Match(name);
        if (!match.Success) return null;
        return $"{match.Groups[1].Value}{int.Parse(match.Groups[2].Value):00}{int.Parse(match.Groups[3].Value):00}{match.Groups[4].Value}";
    }

    private static string PowerShellPath() => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [GeneratedRegex(@"(?:%dp0%|%~dp0|\$basedir)[\\/]+(?<script>node_modules[\\/][^""'\s]+?\.(?:js|cjs|mjs))", RegexOptions.IgnoreCase)]
    private static partial Regex NpmScriptPattern();

    [GeneratedRegex(@"^(\d{4})\.(\d{1,2})\.(\d{1,2})((?:-\d{2}-\d{2}-\d{2})?)-[a-f0-9]+$")]
    private static partial Regex VersionPattern();
}
