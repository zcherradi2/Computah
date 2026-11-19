using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;

namespace Assist;

/// Computah's own agent harness: talks to Claude through the Anthropic API (the user's API key) and runs the tools
/// itself, inside Computah's permissions. No external console. Same events as the console sessions, so the bubble,
/// cards, change cards, ghosts and command log work unchanged.
sealed class BuiltinSession : IAgentSession
{
    public const string DefaultModel = "claude-opus-5-5";
    const int MaxSteps = 40;           // model calls per user message (tool loop)
    const int MaxOutputChars = 30000;  // per tool result

    public string? SessionId { get; private set; }
    public event Action<string>? Text;
    public event Action<string>? Status;
    public event Action<bool, string?>? TurnDone;
    public event Action<string, string>? ToolUsed;
    public event Action<FileChange>? FileChanged;
    public Func<string>? LogName { get; set; }
    public Func<string, string, Task<bool>>? Approve { get; set; }

    readonly Settings settings;
    readonly AgentInfo agent;
    readonly string model;
    readonly BuiltinProvider provider;
    readonly List<string> readable = new(), editable = new();
    readonly string shellDir;
    readonly List<Turn> history;
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim busy = new(1, 1);
    readonly HashSet<string> approvedCommands = new(StringComparer.OrdinalIgnoreCase);
    bool disposed;

    string Who => LogName?.Invoke() ?? SessionId ?? "new session";

    public BuiltinSession(Settings s, AgentInfo agent, string? resumeId, IReadOnlyList<string> sharedPaths)
    {
        settings = s;
        this.agent = agent;
        provider = agent.Provider;
        model = s.ModelFor(agent.Id) is { Length: > 0 } m ? m : provider.DefaultModel;

        var (read, edit) = SharedAccess.Split(sharedPaths);
        readable.AddRange(read.Concat(edit).Select(Full));
        readable.Add(Full(Paths.Workspace));
        editable.AddRange(edit.Select(Full));
        editable.Add(Full(Paths.Workspace)); // its own scratch folder
        shellDir = edit.Concat(read).FirstOrDefault(Directory.Exists) ?? Paths.Workspace;

        history = resumeId != null ? Threads.Load(resumeId) ?? new() : new();
        SessionId = resumeId != null && history.Count > 0 ? resumeId : Guid.NewGuid().ToString("N");
    }

    // ---------- the loop ----------

    public void Send(List<object> content)
    {
        var user = new Turn { Role = "user", Blocks = FromWire(content) };
        _ = Task.Run(() => RunTurn(user));
    }

    async Task RunTurn(Turn user)
    {
        await busy.WaitAsync();
        try
        {
            history.Add(user);
            AnthropicClient client;
            try { client = BuiltinProviders.Client(provider); }
            catch (Exception ex) { Done(true, NoKey(ex)); return; }

            for (int step = 0; step < MaxSteps && !disposed; step++)
            {
                Status?.Invoke(step == 0 ? "Thinking…" : "Working…");
                Message msg;
                try { msg = await Stream(client, step > 0); }
                catch (Exception ex) when (!disposed)
                {
                    Done(true, IsAuth(ex) ? NoKey(ex) : "Claude API error: " + ex.Message);
                    return;
                }
                if (disposed) return;

                var assistant = ToTurn(msg);
                history.Add(assistant);

                if (msg.StopReason == "refusal")
                {
                    Done(true, "Claude declined this request" + (msg.StopDetails?.Explanation is { Length: > 0 } e ? ": " + e : "."));
                    return;
                }
                var calls = assistant.Blocks.Where(b => b.Type == "tool_use").ToList();
                if (calls.Count == 0 || msg.StopReason != "tool_use")
                {
                    if (msg.StopReason == "max_tokens") Text?.Invoke("\n\n(The answer was cut off: it hit the length limit.)");
                    Done(false, null);
                    return;
                }

                // Run the tools; one result per call, all in a single user message.
                var results = new Turn { Role = "user" };
                foreach (var call in calls)
                {
                    if (disposed) return;
                    var (output, failed) = await RunTool(call);
                    results.Blocks.Add(new Block { Type = "tool_result", ToolUseId = call.Id, Text = output, IsError = failed });
                }
                history.Add(results);
                Threads.Save(SessionId!, history);
            }
            Text?.Invoke("\n\n(Stopped after many steps. Ask me to continue if needed.)");
            Done(false, null);
        }
        catch (Exception ex) { Paths.Log("builtin turn: " + ex); Done(true, ex.Message); }
        finally { busy.Release(); }
    }

    void Done(bool error, string? message)
    {
        if (SessionId != null && history.Count > 0) Threads.Save(SessionId, history);
        Status?.Invoke("");
        TurnDone?.Invoke(error, message);
    }

    /// One model call, streamed: text goes to the bubble as it arrives; the aggregator gives the full message.
    async Task<Message> Stream(AnthropicClient client, bool continuing)
    {
        var p = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 32000,
            System = new List<TextBlockParam> { new() { Text = SystemPrompt(), CacheControl = new CacheControlEphemeral() } },
            Tools = Tools(),
            Messages = history.Select(ToParam).ToList(),
            CacheControl = new CacheControlEphemeral(), // cache the conversation so far, too
            OutputConfig = provider.Effort ? new OutputConfig { Effort = Effort.Medium } : null,
        };
        var agg = new MessageContentAggregator();
        bool textBefore = continuing, inText = false;
        await foreach (var ev in client.Messages.CreateStreaming(p, cts.Token).CollectAsync(agg))
        {
            if (ev.TryPickContentBlockStart(out var start))
            {
                if (start.ContentBlock.TryPickText(out _))
                {
                    if (textBefore) Text?.Invoke("\n\n");
                    inText = true;
                }
                else if (start.ContentBlock.TryPickToolUse(out var tu)) Status?.Invoke(Label(tu.Name));
            }
            else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t) && t.Text.Length > 0)
            {
                Status?.Invoke("");
                Text?.Invoke(t.Text);
                textBefore = true;
            }
            else if (ev.TryPickContentBlockStop(out _)) inText = false;
        }
        _ = inText;
        return agg.Message();
    }

    string SystemPrompt() => Prompts.Build(settings) + "\n\n" + Prompts.BuiltinTools(shellDir, readable, editable);

    static string Label(string? name) => name switch
    {
        "str_replace_based_edit_tool" => "Working with files…",
        "powershell" => "Running a command…",
        "glob" or "grep" => "Searching files…",
        _ => "Working…",
    };

    static bool IsAuth(Exception ex) => ex.GetType().Name.Contains("Authentication") || ex.Message.Contains("api key", StringComparison.OrdinalIgnoreCase)
                                        || ex.Message.Contains("x-api-key") || ex.Message.Contains("401");

    string NoKey(Exception ex) =>
        $"{agent.Name} needs a working {provider.Name} API key (create one at {provider.KeySite}). Open the tray menu > Choose agent... " +
        "and paste it on this agent's card, or choose another agent. (" + ex.Message + ")";

    // ---------- tools ----------

    static readonly string[] SkipDirs = { ".git", "node_modules", "bin", "obj", ".vs", "dist", "build", ".next", "__pycache__" };

    /// Anthropic's text editor tool where supported; elsewhere the same tool (same name and commands) defined by hand.
    ToolUnion EditorTool() => provider.NativeEditor
        ? new ToolUnion(new ToolTextEditor20250728())
        : new Tool
        {
            Name = "str_replace_based_edit_tool",
            Description = "View, create and edit files. Commands: view (a file with line numbers, or a folder listing; optional view_range [start, end]), " +
                          "create (path + file_text), str_replace (path + old_str + new_str; old_str must match exactly once), " +
                          "insert (path + insert_line + insert_text; inserts after that line, 0 = top).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "view", "create", "str_replace", "insert" } }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Absolute path." }),
                    ["view_range"] = JsonSerializer.SerializeToElement(new { type = "array", items = new { type = "integer" } }),
                    ["file_text"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                    ["old_str"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                    ["new_str"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                    ["insert_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                    ["insert_text"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                },
                Required = ["command", "path"],
            },
        };

    List<ToolUnion> Tools() =>
    [
        EditorTool(),
        new Tool
        {
            Name = "powershell",
            Description = "Run a Windows PowerShell 5.1 command and get its output. Read-only commands (Get-ChildItem, Get-Content, " +
                          "Select-String, git status/log/diff...) run directly; anything that changes files, settings or processes " +
                          "asks the user first, who may refuse. Prefer the file editor tool for editing files.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The PowerShell command." }),
                    ["timeout_seconds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Default 120, max 600." }),
                },
                Required = ["command"],
            },
        },
        new Tool
        {
            Name = "glob",
            Description = "Find files by name pattern (e.g. **/*.cs, src/**/Program.*) under a folder. Newest first, at most 300.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Folder to search (absolute). Default: the first workspace." }),
                },
                Required = ["pattern"],
            },
        },
        new Tool
        {
            Name = "grep",
            Description = "Search file contents with a regular expression. Returns path:line: text, at most 200 matches.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = ".NET regular expression." }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "File or folder (absolute). Default: the first workspace." }),
                    ["glob"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Only files whose name matches, e.g. *.ts" }),
                    ["ignore_case"] = JsonSerializer.SerializeToElement(new { type = "boolean" }),
                },
                Required = ["pattern"],
            },
        },
    ];

    async Task<(string, bool)> RunTool(Block call)
    {
        Dictionary<string, JsonElement> input;
        try { input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.InputJson ?? "{}") ?? new(); }
        catch { return ("Invalid tool input (not JSON).", true); }
        string S(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int? I(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        var name = call.Name ?? "";
        var detail = name switch
        {
            "powershell" => S("command"),
            "glob" => S("pattern") + (S("path") is { Length: > 0 } gp ? "  in  " + gp : ""),
            "grep" => S("pattern") + (S("path") is { Length: > 0 } rp ? "  in  " + rp : ""),
            _ => $"{S("command")} {S("path")}".Trim(),
        };
        ToolUsed?.Invoke(name == "str_replace_based_edit_tool" ? (S("command") == "view" ? "Read" : "Edit") : name, detail);
        CommandLog.ToolCall(Who, agent.Name, Permissions(), name, call.InputJson ?? "", call.Id);

        string output; bool failed;
        try
        {
            (output, failed) = name switch
            {
                "str_replace_based_edit_tool" => Editor(S("command"), S("path"), input),
                "powershell" => await PowerShell(S("command"), I("timeout_seconds")),
                "glob" => Glob(S("pattern"), S("path")),
                "grep" => Grep(S("pattern"), S("path"), S("glob"), input.TryGetValue("ignore_case", out var ic) && ic.ValueKind == JsonValueKind.True),
                _ => ($"Unknown tool \"{name}\".", true),
            };
        }
        catch (Exception ex) { (output, failed) = ("Error: " + ex.Message, true); }
        if (output.Length > MaxOutputChars) output = output[..MaxOutputChars] + $"\n… (cut: {output.Length} characters in all)";
        CommandLog.ToolResult(Who, call.Id, failed, output);
        return (output, failed);
    }

    string Permissions() =>
        $"built-in agent ({model}); mouse/keyboard control: {settings.Control}\n" +
        "read: " + string.Join(", ", readable) + "\nedit: " + string.Join(", ", editable) +
        "\nshell: read-only commands run, others need the user's approval";

    // ----- access -----

    static string Full(string p) { try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; } }

    static bool Under(string path, IEnumerable<string> roots) =>
        roots.Any(r => path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));

    string? Resolve(string raw, bool forEdit, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(raw)) return "A path is required.";
        var p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Replace('/', '\\');
        if (!Path.IsPathRooted(p)) p = Path.Combine(shellDir, p);
        full = Full(p);
        if (Under(full, new[] { Full(Paths.Screenshots) })) return "Computah's screenshots folder is off limits.";
        if (forEdit && !Under(full, editable))
            return $"No edit access to {full}. Ask the user with <request-access path=\"...\" mode=\"edit\" reason=\"...\"/>.";
        if (!forEdit && !Under(full, readable))
            return $"No access to {full}. Ask the user with <request-access path=\"...\" reason=\"...\"/>.";
        return null;
    }

    // ----- file editor (Anthropic's text editor tool) -----

    (string, bool) Editor(string command, string rawPath, Dictionary<string, JsonElement> input)
    {
        string S(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool edit = command is "create" or "str_replace" or "insert";
        if (Resolve(rawPath, edit, out var path) is { } denied) return (denied, true);

        if (command == "view")
        {
            if (Directory.Exists(path))
            {
                var sb = new StringBuilder();
                foreach (var e in Directory.EnumerateFileSystemEntries(path).OrderBy(x => x).Take(400))
                {
                    var n = Path.GetFileName(e);
                    if (Directory.Exists(e)) { sb.AppendLine(n + "\\"); if (!SkipDirs.Contains(n)) foreach (var c in Directory.EnumerateFileSystemEntries(e).OrderBy(x => x).Take(40)) sb.AppendLine("  " + Path.GetFileName(c) + (Directory.Exists(c) ? "\\" : "")); }
                    else sb.AppendLine(n);
                }
                return (sb.Length > 0 ? sb.ToString() : "(empty folder)", false);
            }
            if (!File.Exists(path)) return ($"{path} does not exist.", true);
            var (text, _, _) = ReadText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int from = 1, to = lines.Length;
            if (input.TryGetValue("view_range", out var vr) && vr.ValueKind == JsonValueKind.Array && vr.GetArrayLength() == 2)
            {
                from = Math.Max(1, vr[0].GetInt32());
                int end = vr[1].GetInt32();
                to = end < 0 ? lines.Length : Math.Min(lines.Length, end);
            }
            var outp = new StringBuilder();
            for (int i = from; i <= to; i++) outp.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            return (outp.ToString(), false);
        }

        if (command == "create")
        {
            var before = File.Exists(path) ? ReadText(path) : (null, null, null);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var newText = S("file_text");
            WriteText(path, newText, before.Item2);
            Changed(path, before.Item1, before.Item3, "Write", null, newText);
            return (before.Item1 == null ? $"Created {path}." : $"Overwrote {path}.", false);
        }

        if (!File.Exists(path)) return ($"{path} does not exist.", true);
        var (old, enc, oldBytes) = ReadText(path);
        bool crlf = old.Contains("\r\n");
        string Eol(string s) => crlf ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s;

        if (command == "str_replace")
        {
            var find = Eol(S("old_str"));
            var repl = Eol(S("new_str"));
            if (find.Length == 0) return ("old_str is empty.", true);
            int first = old.IndexOf(find, StringComparison.Ordinal);
            if (first < 0) return ("old_str was not found in the file. View the file again and copy the text exactly.", true);
            if (old.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0) return ("old_str matches more than once. Include more surrounding lines so it is unique.", true);
            var updated = old[..first] + repl + old[(first + find.Length)..];
            WriteText(path, updated, enc);
            Changed(path, old, oldBytes, "Edit", find, repl);
            return ($"Edited {path}.", false);
        }

        if (command == "insert")
        {
            int at = input.TryGetValue("insert_line", out var il) && il.TryGetInt32(out var n) ? n : 0;
            var text = Eol(S("insert_text").Length > 0 ? S("insert_text") : S("new_str"));
            var nl = crlf ? "\r\n" : "\n";
            var lines = old.Split(nl).ToList();
            at = Math.Clamp(at, 0, lines.Count);
            lines.InsertRange(at, text.Split(nl));
            var updated = string.Join(nl, lines);
            WriteText(path, updated, enc);
            Changed(path, old, oldBytes, "Edit", null, text);
            return ($"Inserted after line {at} of {path}.", false);
        }
        return ($"Unknown editor command \"{command}\".", true);
    }

    void Changed(string path, string? before, byte[]? beforeBytes, string tool, string? oldText, string? newText)
    {
        try
        {
            var afterBytes = File.ReadAllBytes(path);
            var (after, _, _) = ReadText(path);
            if (before == after) return;
            FileChanged?.Invoke(new FileChange(path, before, after, tool, oldText, newText, agent.Name, beforeBytes, afterBytes));
        }
        catch (Exception ex) { Paths.Log("builtin change: " + ex.Message); }
    }

    static (string Text, Encoding Enc, byte[] Bytes) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 8_000_000) throw new IOException("File too large (over 8 MB).");
        using var sr = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = sr.ReadToEnd();
        return (text, sr.CurrentEncoding, bytes);
    }

    static void WriteText(string path, string text, Encoding? enc) => File.WriteAllText(path, text, enc ?? new UTF8Encoding(false));

    // ----- PowerShell -----

    static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-childitem", "gci", "ls", "dir", "get-content", "gc", "cat", "type", "select-string", "sls", "test-path",
        "get-item", "gi", "get-itemproperty", "gp", "get-process", "gps", "ps", "get-date", "resolve-path", "rvpa",
        "get-location", "pwd", "gl", "get-command", "gcm", "where.exe", "whoami", "hostname", "select-object", "select",
        "where-object", "where", "?", "sort-object", "sort", "measure-object", "measure", "format-table", "ft",
        "format-list", "fl", "out-string", "group-object", "group", "get-filehash", "get-service", "echo", "write-output",
        "findstr", "foreach-object", "%", "get-unique", "split-path", "join-path", "get-psdrive", "get-computerinfo",
        "get-netipaddress", "get-host", "get-culture", "get-uptime", "tree", "ver", "systeminfo", "tasklist", "netstat", "ipconfig",
    };
    static readonly HashSet<string> GitReadOnly = new(StringComparer.OrdinalIgnoreCase)
        { "status", "log", "diff", "show", "branch", "rev-parse", "remote", "ls-files", "blame", "describe", "tag", "config" };

    static bool IsReadOnly(string command)
    {
        if (Regex.IsMatch(command, @"(^|[^-])>|`|\$\(|&\s*\(|Invoke-|iex\b|Start-Process|Remove-|Set-|New-|Copy-|Move-|Rename-|Out-File|Add-Content|Clear-|Stop-|Kill", RegexOptions.IgnoreCase))
            return false;
        foreach (var seg in command.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var words = seg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            var cmd = words[0];
            if (cmd.Equals("git", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Length < 2 || !GitReadOnly.Contains(words[1])) return false;
                if (words[1].Equals("branch", StringComparison.OrdinalIgnoreCase) && words.Skip(2).Any(w => w.StartsWith("-d", StringComparison.OrdinalIgnoreCase) || w.StartsWith("-m", StringComparison.OrdinalIgnoreCase))) return false;
                if (words[1].Equals("config", StringComparison.OrdinalIgnoreCase) && !words.Skip(2).Any(w => w is "--get" or "--list" or "-l")) return false;
                continue;
            }
            if (words.Length == 2 && words[1] is "--version" or "-v" or "--help") continue;
            if (!ReadOnly.Contains(cmd)) return false;
        }
        return true;
    }

    async Task<(string, bool)> PowerShell(string command, int? timeout)
    {
        if (string.IsNullOrWhiteSpace(command)) return ("No command given.", true);
        if (!IsReadOnly(command) && !approvedCommands.Contains(command))
        {
            var ok = Approve != null && await Approve("Run this command?", command);
            if (!ok) return ("The user did not allow this command. Don't run it again; find another way or ask them.", true);
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = shellDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                                  "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var wait = p.WaitForExitAsync(cts.Token);
        int secs = Math.Clamp(timeout ?? 120, 1, 600);
        if (await Task.WhenAny(wait, Task.Delay(secs * 1000, cts.Token)) != wait)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return ($"Timed out after {secs} s.\n" + await outTask, true);
        }
        var stdout = await outTask; var stderr = await errTask;
        var text = (stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "")).Trim();
        return ((p.ExitCode != 0 ? $"exit code {p.ExitCode}\n" : "") + (text.Length > 0 ? text : "(no output)"), p.ExitCode != 0);
    }

    // ----- glob / grep -----

    static Regex GlobRegex(string glob)
    {
        var g = glob.Replace('/', '\\');
        var sb = new StringBuilder("^");
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*') { sb.Append(".*"); i++; if (i + 1 < g.Length && g[i + 1] == '\\') i++; }
            else if (c == '*') sb.Append(@"[^\\]*");
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }

    IEnumerable<string> Files(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir); dirs = Directory.EnumerateDirectories(dir); } catch { }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) if (!SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) stack.Push(d);
        }
    }

    (string, bool) Glob(string pattern, string rawPath)
    {
        if (Resolve(rawPath.Length > 0 ? rawPath : shellDir, false, out var root) is { } denied) return (denied, true);
        if (!Directory.Exists(root)) return ($"{root} is not a folder.", true);
        var rx = GlobRegex(pattern.Contains('\\') || pattern.Contains('/') || pattern.StartsWith("**") ? pattern : "**/" + pattern);
        var hits = Files(root).Where(f => rx.IsMatch(Path.GetRelativePath(root, f))).Take(5000)
                              .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                              .Take(300).ToList();
        return (hits.Count == 0 ? "No files match." : string.Join("\n", hits), false);
    }

    (string, bool) Grep(string pattern, string rawPath, string glob, bool ignoreCase)
    {
        if (Resolve(rawPath.Length > 0 ? rawPath : shellDir, false, out var root) is { } denied) return (denied, true);
        Regex rx;
        try { rx = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { return ("Bad regular expression: " + ex.Message, true); }
        var nameRx = glob.Length > 0 ? GlobRegex(glob) : null;
        var files = File.Exists(root) ? new[] { root } : Directory.Exists(root) ? Files(root) : Array.Empty<string>();
        var sb = new StringBuilder(); int count = 0;
        foreach (var f in files)
        {
            if (nameRx != null && !nameRx.IsMatch(Path.GetFileName(f))) continue;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > 2_000_000) continue;
                var bytes = File.ReadAllBytes(f);
                if (bytes.Take(4000).Contains((byte)0)) continue; // binary
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (rx.IsMatch(lines[i]))
                    {
                        var line = lines[i].TrimEnd('\r');
                        sb.Append(f).Append(':').Append(i + 1).Append(": ").Append(line.Length > 300 ? line[..300] + "…" : line).Append('\n');
                        if (++count >= 200) return (sb + "… (stopped at 200 matches)", false);
                    }
            }
            catch { }
        }
        return (count == 0 ? "No matches." : sb.ToString(), false);
    }

    // ---------- conversation storage ----------

    /// The conversation in a plain, storable form (turned into SDK types for each request).
    public sealed class Turn
    {
        public string Role { get; set; } = "user";
        public List<Block> Blocks { get; set; } = new();
    }

    public sealed class Block
    {
        public string Type { get; set; } = "text";      // text | image | thinking | redacted_thinking | tool_use | tool_result
        public string? Text { get; set; }
        public string? Signature { get; set; }          // thinking
        public string? Data { get; set; }               // redacted thinking, or image base64
        public string? MediaType { get; set; }          // image
        public string? Id { get; set; }                 // tool_use
        public string? Name { get; set; }
        public string? InputJson { get; set; }
        public string? ToolUseId { get; set; }          // tool_result
        public bool IsError { get; set; }
    }

    /// The bubble's content blocks ({type:"text"} / {type:"image", source:{...}}) as stored blocks.
    static List<Block> FromWire(List<object> content)
    {
        var list = new List<Block>();
        foreach (var o in content)
        {
            var e = JsonSerializer.SerializeToElement(o);
            var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text") list.Add(new Block { Type = "text", Text = e.GetProperty("text").GetString() ?? "" });
            else if (type == "image" && e.TryGetProperty("source", out var src))
                list.Add(new Block { Type = "image", Data = src.GetProperty("data").GetString(), MediaType = src.TryGetProperty("media_type", out var mt) ? mt.GetString() : "image/jpeg" });
        }
        return list;
    }

    static Turn ToTurn(Message msg)
    {
        var turn = new Turn { Role = "assistant" };
        foreach (var b in msg.Content)
        {
            if (b.TryPickText(out var text)) turn.Blocks.Add(new Block { Type = "text", Text = text.Text });
            else if (b.TryPickThinking(out var th)) turn.Blocks.Add(new Block { Type = "thinking", Text = th.Thinking, Signature = th.Signature });
            else if (b.TryPickRedactedThinking(out var rt)) turn.Blocks.Add(new Block { Type = "redacted_thinking", Data = rt.Data });
            else if (b.TryPickToolUse(out var tu))
                turn.Blocks.Add(new Block { Type = "tool_use", Id = tu.ID, Name = tu.Name, InputJson = JsonSerializer.Serialize(tu.Input) });
        }
        return turn;
    }

    static MessageParam ToParam(Turn t)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var b in t.Blocks)
        {
            switch (b.Type)
            {
                case "text": if (!string.IsNullOrEmpty(b.Text)) blocks.Add(new TextBlockParam { Text = b.Text }); break;
                case "image":
                    blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = b.Data ?? "", MediaType = b.MediaType == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg } });
                    break;
                case "thinking": blocks.Add(new ThinkingBlockParam { Thinking = b.Text ?? "", Signature = b.Signature ?? "" }); break;
                case "redacted_thinking": blocks.Add(new RedactedThinkingBlockParam { Data = b.Data ?? "" }); break;
                case "tool_use":
                    blocks.Add(new ToolUseBlockParam
                    {
                        ID = b.Id ?? "", Name = b.Name ?? "",
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.InputJson ?? "{}") ?? new(),
                    });
                    break;
                case "tool_result":
                    blocks.Add(new ToolResultBlockParam { ToolUseID = b.ToolUseId ?? "", Content = b.Text ?? "", IsError = b.IsError });
                    break;
            }
        }
        if (blocks.Count == 0) blocks.Add(new TextBlockParam { Text = "(empty)" });
        return t.Role == "assistant" ? new AssistantMessageParam { Content = blocks } : new UserMessageParam { Content = blocks };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { cts.Cancel(); } catch { }
    }
}

/// Built-in conversations on disk: %LOCALAPPDATA%\Computah\threads\{id}.json
static class Threads
{
    static string Dir => Path.Combine(Path.GetDirectoryName(Paths.Workspace)!, "threads");
    static readonly object Gate = new();

    public static List<BuiltinSession.Turn>? Load(string id)
    {
        try
        {
            var f = Path.Combine(Dir, id + ".json");
            return File.Exists(f) ? JsonSerializer.Deserialize<List<BuiltinSession.Turn>>(File.ReadAllText(f)) : null;
        }
        catch (Exception ex) { Paths.Log("thread load: " + ex.Message); return null; }
    }

    public static void Save(string id, List<BuiltinSession.Turn> turns)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, id + ".json"), JsonSerializer.Serialize(turns));
            }
        }
        catch (Exception ex) { Paths.Log("thread save: " + ex.Message); }
    }
}
