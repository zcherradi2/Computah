using System;
using System.IO;
using System.Linq;
using Anthropic;

namespace Assist;

/// Where Computah's built-in agent sends its requests. All speak the Anthropic Messages API (same SDK), but differ in
/// endpoint, key, models and which Anthropic-specific features they accept.
public sealed record BuiltinProvider(
    string Id,
    string Name,
    string? BaseUrl,        // null = Anthropic itself
    string KeyEnv,          // environment variable holding the key
    string DefaultModel,
    string TitleModel,      // small, fast model for session titles
    bool NativeEditor,      // accepts Anthropic's text_editor tool type (else the same tool is defined as a custom tool)
    bool Effort,            // send output_config.effort
    string KeySite);

static class BuiltinProviders
{
    public static readonly BuiltinProvider Anthropic = new(
        "anthropic", "Claude API", null, "ANTHROPIC_API_KEY", "claude-opus-5-5", "claude-haiku-5-5",
        NativeEditor: true, Effort: true, "console.anthropic.com");

    /// DeepSeek's Anthropic-compatible endpoint (checked: custom tools, images and cache hints work;
    /// Anthropic's text_editor tool type is rejected; output_config.effort switches on its reasoning).
    public static readonly BuiltinProvider DeepSeek = new(
        "deepseek", "DeepSeek", "https://api.deepseek.com/anthropic", "DEEPSEEK_API_KEY", "deepseek-chat", "deepseek-chat",
        NativeEditor: false, Effort: false, "platform.deepseek.com");

    public static readonly BuiltinProvider[] All = { Anthropic, DeepSeek };

    public static BuiltinProvider ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Anthropic;

    public static string? Key(BuiltinProvider p)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
            if (Environment.GetEnvironmentVariable(p.KeyEnv, target) is { Length: > 0 } k) return k.Trim();
        return null;
    }

    public static bool HasKey(BuiltinProvider p) =>
        Key(p) != null ||
        p == Anthropic && (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")) ||
                           Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic")));

    /// A client for this provider. Other providers always get their own key, never Anthropic credentials.
    public static AnthropicClient Client(BuiltinProvider p, string? key = null)
    {
        key ??= Key(p);
        if (p.BaseUrl == null)
            return key != null ? new AnthropicClient { ApiKey = key } : new AnthropicClient();
        if (key == null) throw new InvalidOperationException($"No {p.Name} API key ({p.KeyEnv}).");
        return new AnthropicClient { ApiKey = key, BaseUrl = p.BaseUrl };
    }

    /// Saves a key for this Windows user (future runs) and for the running Computah.
    public static void SaveKey(BuiltinProvider p, string key)
    {
        key = key.Trim();
        Environment.SetEnvironmentVariable(p.KeyEnv, key, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(p.KeyEnv, key, EnvironmentVariableTarget.Process);
    }
}
