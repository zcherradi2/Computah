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
    /// Look: take a screenshot afterwards. Stopped: why it stopped early (the user interrupted, an error).
    /// NeedsAim: actions that were held back because there was no up-to-date screenshot to aim them with (and why);
    /// Computah then takes one and asks the agent to re-issue them.
    /// LookAt: where the next screenshot should be centred (narrow sight), when the look action named a spot.
    public sealed record Result(bool Look, string? Stopped, List<string> Done, List<string>? HeldBack = null, string? HeldBackWhy = null,
                                System.Drawing.Point? LookAt = null, int? LookMonitor = null);

    /// Plain-language description, for the approval card and the report back to the agent.
    public static string Describe(HintTag a) => (a.Get("type") ?? "").ToLowerInvariant() switch
    {
        "click" => $"{(a.Get("count") == "2" ? "Double-click" : a.Get("button") == "right" ? "Right-click" : "Click")} " +
                   (a.Get("element") is { } el ? $"element {el}" : $"at ({a.Get("x")}, {a.Get("y")})"),
        "type" => $"Type \"{Short(a.Get("text") ?? "")}\"" + (a.Get("element") is { } te ? $" into element {te}" : ""),
        "key" => $"Press {a.Get("keys")}",
        "scroll" => $"Scroll {(int.TryParse(a.Get("amount"), out var n) && n < 0 ? "up" : "down")} at ({a.Get("x")}, {a.Get("y")})",
        "drag" => $"Drag from ({a.Get("x1")}, {a.Get("y1")}) to ({a.Get("x2")}, {a.Get("y2")})",
        "move" => $"Move the mouse to ({a.Get("x")}, {a.Get("y")})",
        "wait" => $"Wait {a.Get("ms")} ms",
        "look" => a.Get("monitor") is { } lm ? $"Look at monitor {lm}" : a.Get("element") is { } le ? $"Look around element {le}" : a.Get("x") != null ? $"Look around ({a.Get("x")}, {a.Get("y")})" : "Take a new screenshot",
        "focus" => $"Bring playground {a.Get("playground") ?? a.Get("title")} to the front",
        var t => $"Unknown action \"{t}\"",
    };

    public static bool NeedsScreen(HintTag a) => (a.Get("type") ?? "").ToLowerInvariant() is "click" or "scroll" or "drag" or "move";

    public static async Task<Result> RunAsync(List<HintTag> actions, ScreenMap? map, IntPtr focusTarget, Action<Point>? flash,
                                             Func<HintTag, Task<string?>>? focusPlayground = null)
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

        System.Drawing.Point? lookAt = null;
        int? lookMonitor = null;
        bool stale = map == null; // no screenshot yet, or the screen switched windows since
        string staleWhy = "there was no current screenshot to aim with";
        for (int ai = 0; ai < actions.Count; ai++)
        {
            var a = actions[ai];
            if (Native.IsDown(0x1B)) return new(false, "you pressed Esc", done);
            if (Moved(expected)) return new(false, "you moved the mouse", done);

            var type = (a.Get("type") ?? "").ToLowerInvariant();
            if (type == "look")
            {
                look = true;
                // look monitor="N": the next capture shows that monitor
                if (int.TryParse(a.Get("monitor"), out var mn) && Monitors.Get(mn) != null) { lookMonitor = mn; continue; }
                // look element="N" / x,y: centre the next (narrow) view there
                if (map != null && !stale && (a.Get("element") != null || a.Get("x") != null) && map.ResolveElement(a, "look") && Target(a, "x", "y", map) is { } at)
                    lookAt = new System.Drawing.Point((int)at.X, (int)at.Y);
                continue;
            }
            // element="N": aim at that element (exact). Typing into one clicks it first.
            if (a.Get("element") != null && !stale && map != null)
            {
                if (!map.ResolveElement(a, "click")) { done.Add($"(skipped: element {a.Get("element")} is not in the list)"); continue; }
                if ((a.Get("type") ?? "").ToLowerInvariant() == "type")
                {
                    var at = Target(a, "x", "y", map);
                    if (at != null)
                    {
                        flash?.Invoke(at.Value);
                        await Glide(expected, at.Value);
                        expected = at.Value;
                        await Task.Delay(180);
                        if (Moved(expected)) return new(false, "you moved the mouse", done);
                        Native.Click("left", 1);
                        await Task.Delay(150);
                    }
                }
            }
            if ((NeedsScreen(a) || a.Get("element") != null) && stale)
                return new(true, null, done, actions.Skip(ai).Where(x => (x.Get("type") ?? "") != "look").Select(Describe).ToList(), staleWhy);

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
                case "focus":
                {
                    var err = focusPlayground == null ? "no playgrounds in this session" : await focusPlayground(a);
                    if (err != null) { done.Add($"(couldn't bring playground {a.Get("playground") ?? a.Get("title")} to the front: {err})"); continue; }
                    await Task.Delay(600); // let it come up and repaint before the next step or the screenshot
                    stale = true; staleWhy = "a different window came to the front, so the old screenshot no longer matches";
                    break;
                }
                default:
                    done.Add($"(skipped unknown action \"{type}\")");
                    continue;
            }
            done.Add(Describe(a));
            await Task.Delay(140);
        }
        return new(look, null, done, LookAt: lookAt, LookMonitor: lookMonitor);
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
