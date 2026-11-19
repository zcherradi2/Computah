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

    /// Computah's own agent loop (Claude API with the user's key): nothing to install.
    public bool BuiltIn { get; init; }

    public string? Path { get; set; }
    public bool Installed => BuiltIn || Path != null;

    /// Built-in agents: which API provider they use (Claude API, DeepSeek...).
    public string? ProviderId { get; init; }
    public BuiltinProvider Provider => BuiltinProviders.ById(ProviderId);
    /// Built-in agent: whether a key for its provider is set up.
    public bool HasKey => BuiltIn && BuiltinProviders.HasKey(Provider);
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
            Id = "computah", Name = "Computah (built-in)", Vendor = "Claude API · your Anthropic API key",
            Executables = Array.Empty<string>(), KnownPaths = Array.Empty<string>(),
            InstallHint = "Needs an Anthropic API key: set ANTHROPIC_API_KEY (console.anthropic.com)",
            BuiltIn = true, ProviderId = "anthropic", SupportsSystemPromptFlag = true, SupportsSharedPaths = true,
            Args = (_, _, _) => new List<string>(),
            TitleArgs = () => new List<string>(),
        },
        new AgentInfo
        {
            Id = "computah-deepseek", Name = "Computah (built-in) · DeepSeek", Vendor = "DeepSeek API · your DeepSeek API key",
            Executables = Array.Empty<string>(), KnownPaths = Array.Empty<string>(),
            InstallHint = "Needs a DeepSeek API key: set DEEPSEEK_API_KEY (platform.deepseek.com)",
            BuiltIn = true, ProviderId = "deepseek", SupportsSystemPromptFlag = true, SupportsSharedPaths = true,
            Args = (_, _, _) => new List<string>(),
            TitleArgs = () => new List<string>(),
        },
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

                var (read, edit) = SharedAccess.Split(shared);
                static string Glob(string p) => p.Replace('\\', '/') + (Directory.Exists(p) ? "/**" : "");

                // Shared folders become extra working directories; shared files get a read permission each.
                foreach (var dir in read.Concat(edit).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) { a.Add("--add-dir"); a.Add(dir); }
                var allow = read.Concat(edit).Where(File.Exists).Select(f => $"Read({f.Replace('\\', '/')})").ToList();
                // Paths the user allowed edits on (Edit rules cover every file-editing tool).
                allow.AddRange(edit.Select(p => $"Edit({Glob(p)})"));
                if (!string.IsNullOrWhiteSpace(s.AllowedTools)) allow.AddRange(s.AllowedTools.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (allow.Count > 0) { a.Add("--allowedTools"); a.AddRange(allow); }
                if (!s.LoadMcpServers) a.Add("--strict-mcp-config");
                // Never let the agent open the saved screenshots on its own.
                a.Add("--disallowedTools"); a.Add($"Read({Paths.Screenshots.Replace('\\', '/')}/**)");
                // Everything else that was shared is read-only (a deny would override an edit allowed inside it, so skip those).
                foreach (var p in read.Where(p => !edit.Any(e => e.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                    a.Add($"Edit({Glob(p)})");
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
        if (a.BuiltIn && !a.HasKey) return null; // not usable yet: the picker asks for a key
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
        if (a.BuiltIn) return null;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Where(d => d.Length > 0).Select(d => d.Trim('"'));
        foreach (var dir in dirs)
            foreach (var exe in a.Executables)
            {
                try { var f = System.IO.Path.Combine(dir, exe); if (File.Exists(f)) return f; } catch { }
            }
        return a.KnownPaths.FirstOrDefault(File.Exists);
    }

    /// Makes sure an agent can actually answer before it is chosen. Null = works; otherwise what's wrong and how to fix it.
    /// Built-in: the API key is checked against Anthropic (listing models, no tokens used).
    /// Consoles: a tiny one-shot prompt has to come back (catches "not logged in", broken installs...).
    public static async Task<string?> CheckAsync(AgentInfo a, string? apiKey = null)
    {
        if (a.BuiltIn)
        {
            try
            {
                var prov = a.Provider;
                var client = BuiltinProviders.Client(prov, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
                // Anthropic: list models (no tokens used). Others: a one-token message (models listing may not exist there).
                Task work = prov.BaseUrl == null
                    ? client.Models.List()
                    : client.Messages.Create(new Anthropic.Models.Messages.MessageCreateParams
                      {
                          Model = prov.DefaultModel, MaxTokens = 5,
                          Messages = [new Anthropic.Models.Messages.UserMessageParam { Content = "Say OK" }],
                      });
                if (await Task.WhenAny(work, Task.Delay(30000)) != work) return $"{prov.Name} didn't answer in time. Check your internet connection.";
                await work;
                return null;
            }
            catch (Exception ex)
            {
                var m = ex.Message;
                if (m.Contains("401") || m.Contains("authentication", StringComparison.OrdinalIgnoreCase) || m.Contains("api-key") || m.Contains("api key", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(apiKey)
                        ? $"No working {a.Provider.Name} API key. Paste one below (create it at {a.Provider.KeySite})."
                        : $"{a.Provider.Name} rejected that API key. Check it was copied completely.";
                if (m.Contains("No ") && m.Contains("API key")) return $"No {a.Provider.Name} API key yet. Paste one below (create it at {a.Provider.KeySite}).";
                return $"Couldn't reach {a.Provider.Name}: " + Short(m);
            }
        }

        if (a.Path == null) return $"{a.Name} is not installed. To install: {a.InstallHint}";
        try
        {
            var psi = new ProcessStartInfo(a.Path)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.Workspace,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var x in a.TitleArgs()) psi.ArgumentList.Add(x);
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteAsync("Reply with just the word OK.");
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(90000));
            if (!p.HasExited)
            {
                try { p.Kill(true); } catch { }
                return $"{a.Name} didn't answer within 90 seconds. Run it once in a terminal to check it works.";
            }
            var output = (await outTask).Trim();
            var error = (await errTask).Trim();
            if (p.ExitCode == 0 && output.Length > 0) return null;
            var why = (error.Length > 0 ? error : output);
            var login = why.Contains("login", StringComparison.OrdinalIgnoreCase) || why.Contains("auth", StringComparison.OrdinalIgnoreCase) || why.Contains("api key", StringComparison.OrdinalIgnoreCase);
            return $"{a.Name} isn't working{(login ? " (not signed in?)" : "")}: {Short(why.Length > 0 ? why : $"exit code {p.ExitCode}")}" +
                   $"\nOpen a terminal, run \"{System.IO.Path.GetFileNameWithoutExtension(a.Path)}\" and sign in, then try again.";
        }
        catch (Exception ex) { return $"Couldn't start {a.Name}: {Short(ex.Message)}"; }
    }

    static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 220 ? s[..220] + "…" : s;
    }

    /// Saves an API key for the built-in agent: for this user (future runs) and for this running Computah.
    public static void SaveApiKey(AgentInfo a, string key) => BuiltinProviders.SaveKey(a.Provider, key);

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
