using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assist;

/// A console agent Computah can drive. All supported agents speak the same
/// stream-json protocol (one long-lived process, JSON lines in and out).
public sealed class AgentInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required string[] Executables { get; init; }       // file names searched on PATH
    public required string[] KnownPaths { get; init; }        // usual install locations
    public required string InstallHint { get; init; }
    public bool SupportsSystemPromptFlag { get; init; }       // else instructions go in the first message
    /// (settings, resume session id, shared files/folders) → command-line arguments.
    public required Func<Settings, string?, IReadOnlyList<string>, List<string>> Args { get; init; }
    /// Whether shared files/folders are granted real access (otherwise they are only mentioned).
    public bool SupportsSharedPaths { get; init; }
    /// One-shot, text-only, unsaved run used to name a session. The prompt is sent on stdin.
    public required Func<List<string>> TitleArgs { get; init; }

    public string? Path { get; set; }
    public bool Installed => Path != null;
}

static class Agents
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// Fresh, undetected definitions of every supported agent.
    public static List<AgentInfo> All() => new()
    {
        new AgentInfo
        {
            Id = "claude", Name = "Claude Code", Vendor = "Anthropic",
            Executables = new[] { "claude.exe", "claude.cmd" },
            KnownPaths = new[] { System.IO.Path.Combine(Home, ".local", "bin", "claude.exe"), System.IO.Path.Combine(Roaming, "npm", "claude.cmd") },
            InstallHint = "irm https://claude.ai/install.ps1 | iex",
            SupportsSystemPromptFlag = true,
            SupportsSharedPaths = true,
            Args = (s, resume, shared) =>
            {
                var a = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
                if (resume != null) { a.Add("--resume"); a.Add(resume); }
                var model = s.ModelFor("claude");
                if (!string.IsNullOrWhiteSpace(model)) { a.Add("--model"); a.Add(model); }
                a.Add("--append-system-prompt"); a.Add(Prompts.Build(s));

                // Dropped folders become extra working directories; dropped files get a read permission each.
                foreach (var dir in shared.Where(Directory.Exists)) { a.Add("--add-dir"); a.Add(dir); }
                var allow = shared.Where(File.Exists).Select(f => $"Read({f.Replace('\\', '/')})").ToList();
                if (!string.IsNullOrWhiteSpace(s.AllowedTools)) allow.AddRange(s.AllowedTools.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (allow.Count > 0) { a.Add("--allowedTools"); a.AddRange(allow); }
                if (!s.LoadMcpServers) a.Add("--strict-mcp-config");
                // Never let the agent open the saved screenshots on its own.
                a.Add("--disallowedTools"); a.Add($"Read({Paths.Screenshots.Replace('\\', '/')}/**)");
                // Shared files and folders are read-only for the agent (Edit rules cover every file-editing tool).
                foreach (var p in shared)
                    a.Add($"Edit({p.Replace('\\', '/') + (Directory.Exists(p) ? "/**" : "")})");
                return a;
            },
            TitleArgs = () => new List<string> { "-p", "--model", "haiku", "--no-session-persistence", "--tools", "", "--strict-mcp-config" },
        },
        new AgentInfo
        {
            Id = "qwen", Name = "Qwen Code", Vendor = "Alibaba Qwen",
            Executables = new[] { "qwen.exe", "qwen.cmd" },
            KnownPaths = new[] { System.IO.Path.Combine(Local, "qwen-code", "bin", "qwen.cmd"), System.IO.Path.Combine(Roaming, "npm", "qwen.cmd") },
            InstallHint = "npm install -g @qwen-code/qwen-code",
            SupportsSystemPromptFlag = false,
            Args = (s, resume, shared) =>
            {
                var a = new List<string> { "--input-format", "stream-json", "--output-format", "stream-json", "--include-partial-messages" };
                if (resume != null) { a.Add("--resume"); a.Add(resume); }
                var model = s.ModelFor("qwen");
                if (!string.IsNullOrWhiteSpace(model)) { a.Add("--model"); a.Add(model); }
                return a;
            },
            TitleArgs = () => new List<string> { "-p", "Reply with only the title." },
        },
    };

    /// All agents with Path filled in where found.
    public static List<AgentInfo> Detect()
    {
        var list = All();
        foreach (var a in list) a.Path = Locate(a);
        return list;
    }

    /// The agent chosen in settings, if it is still installed.
    public static AgentInfo? Resolve(Settings s)
    {
        var a = All().FirstOrDefault(x => x.Id == s.Agent);
        if (a == null) return null;
        a.Path = Locate(a);
        return a.Installed ? a : null;
    }

    /// A specific agent by id, if installed.
    public static AgentInfo? ById(string id)
    {
        var a = All().FirstOrDefault(x => x.Id == id);
        if (a == null) return null;
        a.Path = Locate(a);
        return a.Installed ? a : null;
    }

    public static string NameOf(string id) => All().FirstOrDefault(x => x.Id == id)?.Name ?? id;

    static string? Locate(AgentInfo a)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Where(d => d.Length > 0).Select(d => d.Trim('"'));
        foreach (var dir in dirs)
            foreach (var exe in a.Executables)
            {
                try { var f = System.IO.Path.Combine(dir, exe); if (File.Exists(f)) return f; } catch { }
            }
        return a.KnownPaths.FirstOrDefault(File.Exists);
    }

    public static async Task<string?> VersionAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "--version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(15000)) != read) { try { p.Kill(true); } catch { } return null; }
            var line = (await read).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return line;
        }
        catch { return null; }
    }
}
