using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Assist;

static class Paths
{
    public static readonly string Root =
        typeof(Paths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ProjectDir")?.Value is string d && Directory.Exists(d)
            ? d : AppContext.BaseDirectory;

    public static string Screenshots => Path.Combine(Root, "screenshots");

    /// Empty folder Claude runs in: outside any git repo and away from the screenshots,
    /// so Claude can only see what the user actually attaches.
    public static string Workspace
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computah", "workspace");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "assist.log");

    public static void Log(string msg)
    {
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}"); } catch { }
    }
}

public sealed class Settings
{
    /// "sonnet", "haiku", "opus", a full model id, or "" for your Claude Code default.
    public string Model { get; set; } = "sonnet";

    /// What Claude is asked when you press Ctrl+Alt+A.
    public string Instruction { get; set; } =
        "Look at my screen around the mouse pointer, plus any selected text and clipboard content. " +
        "Work out what I most likely need help with and help directly: answer, explain, fix, translate or suggest the next step. Be concise.";

    public string SystemPrompt { get; set; } =
        "You are a fast desktop assistant shown in a small floating chat bubble next to the user's mouse. " +
        "Answer briefly and directly. Use plain text: short paragraphs and simple '-' bullets. " +
        "No headings, no tables, no bold/italics markup. Code may go in plain indented lines.";

    public bool CaptureSelection { get; set; } = true;  // sends Ctrl+C to grab selected text (clipboard is restored)
    public bool SendCursorCrop { get; set; } = true;     // extra full-resolution crop around the pointer
    public bool LoadMcpServers { get; set; } = false;    // MCP servers slow down startup
    public string AllowedTools { get; set; } = "";       // e.g. "Read WebSearch WebFetch"

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Opts) ?? new Settings();
        }
        catch (Exception ex) { Paths.Log("settings.json invalid, using defaults: " + ex.Message); return new Settings(); }

        var s = new Settings();
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(s, Opts)); } catch { }
        return s;
    }
}
