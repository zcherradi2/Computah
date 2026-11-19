using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assist;

/// Names a session from its first exchange, using a quick one-shot run of the same agent.
static class Titles
{
    public static async Task<string?> GenerateAsync(AgentInfo agent, string userText, string answer)
    {
        if (agent.Path == null) return null;
        var prompt =
            "Write a short title (3 to 6 words) for the conversation below, in the language the user wrote in. " +
            "It should say what the user wanted help with. Reply with only the title: no quotes, no trailing period.\n\n" +
            $"User: {Clip(userText, 800)}\n\nAssistant: {Clip(answer, 1500)}";

        try
        {
            var psi = new ProcessStartInfo(agent.Path)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.Workspace,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in agent.TitleArgs()) psi.ArgumentList.Add(a);
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteAsync(prompt);
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(60000)) != read)
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? Clean(await read) : null;
        }
        catch (Exception ex) { Paths.Log("title: " + ex.Message); return null; }
    }

    static string? Clean(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) return null;
        line = line.Trim('"', '\'', '*', '#', '`', ' ', '“', '”').TrimEnd('.');
        if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) line = line[6..].Trim();
        if (line.Length == 0) return null;
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
