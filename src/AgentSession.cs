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

    public AgentSession(Settings s, AgentInfo agent, string? resumeId = null)
    {
        SessionId = resumeId;
        if (!agent.SupportsSystemPromptFlag && resumeId == null)
            instructions = Prompts.Build(s);

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
        foreach (var a in agent.Args(s, resumeId)) psi.ArgumentList.Add(a);

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
        if (instructions != null && SessionId == null && !sentInstructions)
        {
            content = new List<object>(content);
            content.Insert(0, new { type = "text", text = "Instructions for this whole conversation:\n" + instructions });
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
                if (Str(r, "subtype") == "init") SessionId = Str(r, "session_id");
                break;

            case "stream_event":
                if (!r.TryGetProperty("event", out var ev)) break;
                var et = Str(ev, "type");
                if (et == "content_block_start" && ev.TryGetProperty("content_block", out var cb))
                {
                    var ct = Str(cb, "type");
                    if (ct == "text") { if (turnHasText) Text?.Invoke("\n\n"); }
                    else if (ct is "tool_use" or "server_tool_use") Status?.Invoke(ToolLabel(Str(cb, "name")));
                    else if (ct is "thinking" or "redacted_thinking") Status?.Invoke("Thinking…");
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
