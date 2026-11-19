using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Assist;

/// One long-lived agent process (Claude Code, Qwen Code, …) in stream-json mode = one conversation.
/// Follow-up messages are written to the same process, so context is kept and replies start fast.
sealed class AgentSession : IDisposable
{
    readonly Process proc;
    readonly StringBuilder stderr = new();
    readonly string? instructions; // sent with the first message when the agent has no system-prompt flag
    volatile bool inTurn, turnHasText, disposed;
    bool sentInstructions;

    public string? SessionId { get; private set; }
    public event Action<string>? Text;          // background thread
    public event Action<string>? Status;        // background thread
    public event Action<bool, string?>? TurnDone; // (isError, message) background thread
    /// A tool call the agent made: (tool name, the command / path / query it ran with). Background thread.
    public event Action<string, string>? ToolUsed;

    /// The agent changed a file (its edit succeeded). Background thread.
    public event Action<FileChange>? FileChanged;

    // file edits in flight: stream index → (tool id, name, input JSON so far); tool id → the file before the edit
    readonly Dictionary<int, (string Id, string Name, StringBuilder Json)> streamingTools = new();
    readonly Dictionary<string, FileChange> edits = new();

    static bool IsEditTool(string? name) => name is "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "write_file" or "replace";

    void Snapshot(string id, string tool, JsonElement input)
    {
        if (id.Length == 0 || edits.ContainsKey(id) || input.ValueKind != JsonValueKind.Object) return;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path") ?? Str(input, "absolute_path");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathRooted(path)) path = Path.Combine(Paths.Workspace, path);
        string? before = null;
        byte[]? bytes = null;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 20_000_000)
            {
                bytes = File.ReadAllBytes(path);
                using var sr = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
                before = sr.ReadToEnd();
            }
        }
        catch { }
        edits[id] = new FileChange(Path.GetFullPath(path), before, null, tool, Str(input, "old_string"), Str(input, "new_string"), agentName, bytes);
    }

    void FinishChange(FileChange c)
    {
        string? after = null;
        byte[]? afterBytes = null;
        try
        {
            if (File.Exists(c.Path))
            {
                afterBytes = File.ReadAllBytes(c.Path);
                using var sr = new StreamReader(new MemoryStream(afterBytes), detectEncodingFromByteOrderMarks: true);
                after = sr.ReadToEnd();
            }
        }
        catch { }
        if (after == null) return;
        c = c with { AfterBytes = afterBytes };
        var before = c.Before;
        // Lost the race (the edit ran before we read the file): rebuild "before" from the edit itself when possible.
        if (before == after && c.OldText != null && c.NewText != null && c.NewText.Length > 0)
        {
            int i = after.IndexOf(c.NewText, StringComparison.Ordinal);
            if (i >= 0 && after.IndexOf(c.NewText, i + 1, StringComparison.Ordinal) < 0)
            {
                before = after[..i] + c.OldText + after[(i + c.NewText.Length)..];
                c = c with { BeforeBytes = null }; // the saved bytes are already the edited file
            }
        }
        if (before == after) return;
        FileChanged?.Invoke(c with { Before = before, After = after });
    }

    /// Which conversation this is, for the command log ("title (id)").
    public Func<string>? LogName;
    readonly string agentName = "", permissions = "";
    string? reportedMode; // from the agent's own startup message
    string Permissions => reportedMode == null ? permissions : permissions + "\n" + reportedMode;
    string Who => LogName?.Invoke() ?? SessionId ?? "new session";

    /// `refreshInstructions`: resuming after the instructions changed (e.g. control was just allowed);
    /// agents without a system-prompt flag then get the new instructions with the next message.
    public AgentSession(Settings s, AgentInfo agent, string? resumeId, bool refreshInstructions, IReadOnlyList<string> sharedPaths)
    {
        SessionId = resumeId;
        if (!agent.SupportsSystemPromptFlag && (resumeId == null || refreshInstructions))
            instructions = "Instructions for this whole conversation:\n" + Prompts.Build(s);
        else if (resumeId != null)
            // A resumed conversation keeps the system prompt it started with (agents ignore a new one on resume),
            // so the current Computah instructions (features, file access, control permission) are restated.
            instructions = "[Computah update: these are the current Computah instructions; they replace any earlier ones]\n" +
                           Prompts.Build(s);

        var psi = new ProcessStartInfo(agent.Path ?? throw new FileNotFoundException($"{agent.Name} is not installed"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Paths.Workspace,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var args = agent.Args(s, resumeId, sharedPaths);
        foreach (var a in args) psi.ArgumentList.Add(a);
        agentName = agent.Name;
        permissions = CommandLog.DescribePermissions(s, agent, args);

        // Don't let a parent Claude Code session's env leak in.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {agent.Name}");
        Task.Run(ReadLoop);
        Task.Run(async () =>
        {
            string? l;
            while ((l = await proc.StandardError.ReadLineAsync()) != null)
                lock (stderr) { if (stderr.Length < 8000) stderr.AppendLine(l); }
        });
    }

    public void Send(List<object> content)
    {
        turnHasText = false;
        inTurn = true;
        if (instructions != null && !sentInstructions)
        {
            content = new List<object>(content);
            content.Insert(0, new { type = "text", text = instructions });
            sentInstructions = true;
        }
        var msg = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object> { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
            ["session_id"] = SessionId ?? "",
        };
        proc.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
        proc.StandardInput.Flush();
    }

    async Task ReadLoop()
    {
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try { Handle(line); } catch (Exception ex) { Paths.Log("parse: " + ex.Message); }
            }
        }
        catch (Exception ex) { Paths.Log("read loop: " + ex.Message); }

        if (inTurn && !disposed)
        {
            inTurn = false;
            string err; lock (stderr) err = stderr.ToString().Trim();
            TurnDone?.Invoke(true, "The agent exited unexpectedly." +(err.Length > 0 ? "\n" + Tail(err, 600) : ""));
        }
    }

    void Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        switch (Str(r, "type"))
        {
            case "system":
                if (Str(r, "subtype") == "init")
                {
                    SessionId = Str(r, "session_id");
                    if (Str(r, "permissionMode") is { } pm) reportedMode = $"{agentName} permission mode: {CommandLog.ExplainMode(pm)}";
                }
                break;

            case "stream_event":
                if (!r.TryGetProperty("event", out var ev)) break;
                var et = Str(ev, "type");
                if (et == "content_block_start" && ev.TryGetProperty("content_block", out var cb))
                {
                    var ct = Str(cb, "type");
                    if (ct == "text") { if (turnHasText) Text?.Invoke("\n\n"); }
                    else if (ct is "tool_use" or "server_tool_use")
                    {
                        Status?.Invoke(ToolLabel(Str(cb, "name")));
                        if (ev.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var bi) && IsEditTool(Str(cb, "name")))
                            streamingTools[bi] = (Str(cb, "id") ?? "", Str(cb, "name")!, new StringBuilder());
                    }
                    else if (ct is "thinking" or "redacted_thinking") Status?.Invoke("Thinking…");
                }
                else if (et == "content_block_delta" && ev.TryGetProperty("delta", out var jd) && Str(jd, "type") == "input_json_delta"
                         && ev.TryGetProperty("index", out var jx) && jx.TryGetInt32(out var ji) && streamingTools.TryGetValue(ji, out var st))
                    st.Json.Append(Str(jd, "partial_json"));
                else if (et == "content_block_stop" && ev.TryGetProperty("index", out var sx) && sx.TryGetInt32(out var si)
                         && streamingTools.Remove(si, out var done))
                {
                    // The edit's arguments are complete but it hasn't run yet: keep the file as it is now.
                    try
                    {
                        using var inputDoc = JsonDocument.Parse(done.Json.Length > 0 ? done.Json.ToString() : "{}");
                        Snapshot(done.Id, done.Name, inputDoc.RootElement);
                    }
                    catch (Exception ex) { Paths.Log("edit snapshot: " + ex.Message); }
                }
                else if (et == "content_block_delta" && ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta")
                {
                    var t = Str(d, "text");
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (!turnHasText) { turnHasText = true; Status?.Invoke(""); }
                        Text?.Invoke(t);
                    }
                }
                break;

            case "assistant":
                if (r.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (Str(block, "type") is "tool_use" or "server_tool_use")
                        {
                            var name = Str(block, "name") ?? "tool";
                            var input = block.TryGetProperty("input", out var inp) ? inp : default;
                            var detail = ToolDetail(name, input);
                            CommandLog.ToolCall(Who, agentName, Permissions, name, FullDetail(name, input, detail), Str(block, "id"));
                            ToolUsed?.Invoke(name, detail);
                            if (IsEditTool(name) && Str(block, "id") is { } eid && !edits.ContainsKey(eid)) Snapshot(eid, name, input); // fallback
                        }
                break;

            case "user": // tool results come back as a user message
                if (r.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array)
                    foreach (var block in uc.EnumerateArray())
                        if (Str(block, "type") == "tool_result")
                        {
                            bool failed = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                            var rid = Str(block, "tool_use_id");
                            CommandLog.ToolResult(Who, rid, failed, ResultText(block));
                            if (rid != null && edits.Remove(rid, out var change) && !failed) FinishChange(change);
                        }
                break;

            case "result":
                inTurn = false;
                bool isErr = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                string? errMsg = null;
                if (isErr)
                {
                    errMsg = Str(r, "result");
                    if (errMsg == null && r.TryGetProperty("error", out var er)) errMsg = Str(er, "message");
                    errMsg ??= Str(r, "subtype") ?? "Unknown error";
                }
                TurnDone?.Invoke(isErr, errMsg);
                break;
        }
    }

    static string ToolLabel(string? name) => name switch
    {
        "Read" => "Reading a file…",
        "WebSearch" or "web_search" => "Searching the web…",
        "WebFetch" or "web_fetch" => "Opening a page…",
        "Bash" or "PowerShell" => "Running a command…",
        "Grep" or "Glob" => "Searching files…",
        null => "Working…",
        _ => $"Using {name}…",
    };

    /// The interesting part of a tool call's input: the shell command, file path, search, URL…
    static string ToolDetail(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return name;
        foreach (var key in new[] { "command", "file_path", "notebook_path", "url", "query", "pattern", "path", "description", "prompt" })
            if (Str(input, key) is { Length: > 0 } v)
                return key == "pattern" && Str(input, "path") is { Length: > 0 } where ? $"{v}  in  {where}" : v;
        var raw = input.GetRawText();
        return raw.Length > 2000 ? raw[..2000] + "…" : raw;
    }

    /// For the log: the whole input when there is more to it than the headline (e.g. an edit's old/new text).
    static string FullDetail(string name, JsonElement input, string headline)
    {
        if (input.ValueKind != JsonValueKind.Object) return headline;
        var extra = input.EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() != headline)
            .Select(p => $"{p.Name} = {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}")
            .ToList();
        var all = extra.Count == 0 ? headline : headline + "\n" + string.Join("\n", extra);
        return all.Length > 6000 ? all[..6000] + "â€¦" : all;
    }

    static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
            return string.Join("\n", c.EnumerateArray().Select(x => Str(x, "text") ?? (Str(x, "type") == "image" ? "[image]" : "")).Where(x => x.Length > 0));
        return "";
    }

    static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        proc.Dispose();
    }
}
