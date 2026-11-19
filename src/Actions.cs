using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Assist;

/// Runs the agent's &lt;action/&gt; tags with the real mouse and keyboard.
/// Stops as soon as the user moves the mouse or presses Esc.
static class ActionRunner
{
    public sealed record Result(bool Look, string? Stopped, List<string> Done);

    /// Plain-language description, for the approval card and the report back to the agent.
    public static string Describe(HintTag a) => (a.Get("type") ?? "").ToLowerInvariant() switch
    {
        "click" => $"{(a.Get("count") == "2" ? "Double-click" : a.Get("button") == "right" ? "Right-click" : "Click")} at ({a.Get("x")}, {a.Get("y")})",
        "type" => $"Type \"{Short(a.Get("text") ?? "")}\"",
        "key" => $"Press {a.Get("keys")}",
        "scroll" => $"Scroll {(int.TryParse(a.Get("amount"), out var n) && n < 0 ? "up" : "down")} at ({a.Get("x")}, {a.Get("y")})",
        "drag" => $"Drag from ({a.Get("x1")}, {a.Get("y1")}) to ({a.Get("x2")}, {a.Get("y2")})",
        "move" => $"Move the mouse to ({a.Get("x")}, {a.Get("y")})",
        "wait" => $"Wait {a.Get("ms")} ms",
        "look" => "Take a new screenshot",
        var t => $"Unknown action \"{t}\"",
    };

    public static bool NeedsScreen(HintTag a) => (a.Get("type") ?? "").ToLowerInvariant() is "click" or "scroll" or "drag" or "move";

    public static async Task<Result> RunAsync(List<HintTag> actions, ScreenMap? map, IntPtr focusTarget, Action<Point>? flash)
    {
        var done = new List<string>();
        bool look = false;
        Native.GetCursorPos(out var start);
        var expected = new Point(start.X, start.Y);

        // Keyboard-only batches go to the app the user was in, not to Computah's bubble.
        var first = actions.FirstOrDefault(a => (a.Get("type") ?? "") != "wait");
        if (first != null && !NeedsScreen(first) && focusTarget != IntPtr.Zero)
        {
            Native.ForceForeground(focusTarget);
            await Task.Delay(150);
        }

        foreach (var a in actions)
        {
            if (Native.IsDown(0x1B)) return new(false, "you pressed Esc", done);
            if (Moved(expected)) return new(false, "you moved the mouse", done);

            var type = (a.Get("type") ?? "").ToLowerInvariant();
            if (type == "look") { look = true; continue; }
            if (NeedsScreen(a) && map == null) return new(false, "no screenshot to aim with", done);

            switch (type)
            {
                case "click":
                case "move":
                {
                    var p = Target(a, "x", "y", map!);
                    if (p == null) continue;
                    flash?.Invoke(p.Value);
                    await Glide(expected, p.Value);
                    expected = p.Value;
                    await Task.Delay(type == "click" ? 220 : 60);
                    if (Moved(expected)) return new(false, "you moved the mouse", done);
                    if (type == "click")
                        Native.Click(a.Get("button")?.ToLowerInvariant() ?? "left", int.TryParse(a.Get("count"), out var c) ? c : 1);
                    break;
                }
                case "scroll":
                {
                    var p = Target(a, "x", "y", map!);
                    if (p == null) continue;
                    await Glide(expected, p.Value);
                    expected = p.Value;
                    int amount = int.TryParse(a.Get("amount"), out var n) ? Math.Clamp(n, -30, 30) : 3;
                    for (int i = 0; i < Math.Abs(amount); i++) { Native.Wheel(Math.Sign(amount)); await Task.Delay(35); }
                    break;
                }
                case "drag":
                {
                    var p1 = Target(a, "x1", "y1", map!);
                    var p2 = Target(a, "x2", "y2", map!);
                    if (p1 == null || p2 == null) continue;
                    await Glide(expected, p1.Value);
                    Native.MouseDown();
                    await Task.Delay(80);
                    await Glide(p1.Value, p2.Value, 18);
                    await Task.Delay(60);
                    Native.MouseUp();
                    expected = p2.Value;
                    break;
                }
                case "type":
                    Native.TypeText(a.Get("text") ?? "");
                    break;
                case "key":
                    if (!Native.PressKeys(a.Get("keys") ?? "")) { done.Add($"(skipped unknown keys \"{a.Get("keys")}\")"); continue; }
                    break;
                case "wait":
                    await Task.Delay(Math.Clamp(int.TryParse(a.Get("ms"), out var ms) ? ms : 500, 0, 5000));
                    break;
                default:
                    done.Add($"(skipped unknown action \"{type}\")");
                    continue;
            }
            done.Add(Describe(a));
            await Task.Delay(140);
        }
        return new(look, null, done);
    }

    static Point? Target(HintTag a, string kx, string ky, ScreenMap map)
    {
        double x = a.Num(kx), y = a.Num(ky);
        if (double.IsNaN(x) || double.IsNaN(y)) return null;
        var p = map.ToScreen(x, y, a.Image);
        return new Point(Math.Round(p.X), Math.Round(p.Y));
    }

    /// Visible, quick cursor glide so the user sees where Computah is going.
    static async Task Glide(Point from, Point to, int steps = 12)
    {
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps, e = 1 - Math.Pow(1 - t, 3);
            Native.SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * e), (int)Math.Round(from.Y + (to.Y - from.Y) * e));
            await Task.Delay(12);
        }
    }

    static bool Moved(Point expected)
    {
        Native.GetCursorPos(out var c);
        return Math.Abs(c.X - expected.X) > 4 || Math.Abs(c.Y - expected.Y) > 4;
    }

    static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
