using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Assist;

/// Append-only, human-readable log of everything run on the user's behalf:
/// the agent's own tool calls (shell commands, file reads/edits, web…) with their outcome,
/// and the mouse/keyboard actions Computah performed for it — each with the session and its permissions.
/// %LOCALAPPDATA%\Computah\commands.log
public static class CommandLog
{
    public static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computah", "commands.log");

    static readonly object Gate = new();

    /// The permissions an agent process was started with, read from its actual command line.
    public static string DescribePermissions(Settings s, AgentInfo agent, IReadOnlyList<string> args)
    {
        var parts = new List<string> { $"mouse/keyboard control: {s.Control}" };
        string? permMode = null;
        var allowed = new List<string>(); var denied = new List<string>(); var dirs = new List<string>();
        List<string>? into = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                into = a switch { "--allowedTools" => allowed, "--disallowedTools" => denied, "--add-dir" => dirs, _ => null };
                if (a == "--dangerously-skip-permissions" || a == "--yolo") permMode = "skip all permission checks";
                continue;
            }
            if (into == null) continue;
            into.Add(a);
            if (into == dirs) into = null; // --add-dir takes one value
        }
        int i = args.ToList().IndexOf("--permission-mode");
        if (i >= 0 && i + 1 < args.Count) permMode = args[i + 1];

        if (permMode != null) parts.Add($"{agent.Name} permission flag: {permMode}");
        parts.Add("pre-approved tools: " + (allowed.Count > 0 ? string.Join(", ", allowed) : "none"));
        if (denied.Count > 0) parts.Add("blocked: " + string.Join(", ", denied));
        if (dirs.Count > 0) parts.Add("extra folders: " + string.Join(", ", dirs));
        return string.Join("\n", parts);
    }

    /// What an agent's reported permission mode means in practice.
    public static string ExplainMode(string mode) => mode switch
    {
        "default" => "default (read-only commands run; anything that changes files or the system needs approval and is refused, since nobody can approve it here)",
        "acceptEdits" => "acceptEdits (file edits allowed without asking)",
        "bypassPermissions" => "bypassPermissions (everything allowed without asking)",
        "plan" => "plan (read-only)",
        _ => mode,
    };

    public static void ToolCall(string session, string agentName, string permissions, string tool, string detail, string? id) =>
        Write($"{Stamp()}  {tool}  ·  {session}  ·  {agentName}{(id != null ? $"  ·  call {Short(id)}" : "")}\n" +
              Indent("permissions", permissions) +
              Indent("ran", detail));

    public static void ToolResult(string session, string? id, bool isError, string output) =>
        Write($"{Stamp()}  {(isError ? "FAILED/REFUSED" : "done")}  ·  {session}{(id != null ? $"  ·  call {Short(id)}" : "")}\n" +
              (isError || output.Length > 0 ? Indent(isError ? "error" : "output", Clip(output, isError ? 600 : 300)) : ""));

    public static void Actions(string session, string control, IEnumerable<string> steps, string outcome) =>
        Write($"{Stamp()}  Computah mouse/keyboard  ·  {session}\n" +
              Indent("permissions", $"mouse/keyboard control: {control}{(control == "ask" ? " (approved by the user for this batch)" : "")}") +
              Indent("ran", string.Join("\n", steps)) +
              Indent("outcome", outcome));

    static void Write(string entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                RotateIfBig();
                System.IO.File.AppendAllText(File, entry + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) { Paths.Log("command log: " + ex.Message); }
    }

    /// Keep the log from growing forever: past 5 MB the old one becomes commands.old.log.
    static void RotateIfBig()
    {
        var fi = new FileInfo(File);
        if (!fi.Exists || fi.Length < 5_000_000) return;
        var old = Path.ChangeExtension(File, ".old.log");
        System.IO.File.Delete(old);
        System.IO.File.Move(File, old);
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    static string Short(string id) => id.Length > 12 ? id[^8..] : id;
    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + $"… ({s.Length} chars)";

    static string Indent(string label, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        sb.Append("    ").Append(label).Append(": ").Append(lines[0]).Append('\n');
        var pad = new string(' ', 6 + label.Length);
        foreach (var l in lines.Skip(1)) sb.Append(pad).Append(l).Append('\n');
        return sb.ToString();
    }
}
