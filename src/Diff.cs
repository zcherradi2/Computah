using System;
using System.Collections.Generic;

namespace Assist;

/// A file the agent changed: its content before (null = it didn't exist) and after the edit.
public sealed record FileChange(string Path, string? Before, string? After, string Tool, string? OldText, string? NewText, string AgentName, byte[]? BeforeBytes = null, byte[]? AfterBytes = null);

public enum DiffKind { Same, Removed, Added }

/// One line of a unified diff view. Line numbers are 1-based, 0 when the line isn't in that version.
/// For a changed line (a removed line paired with its replacement), Start/Length mark the characters that differ.
public sealed record DiffLine(DiffKind Kind, string Text, int OldNo, int NewNo, int Start = 0, int Length = -1);

public static class Diff
{
    public static List<DiffLine> Lines(string? before, string? after)
    {
        var a = Split(before);
        var b = Split(after);

        // common head and tail
        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;

        var ops = new List<(DiffKind Kind, int A, int B)>();
        for (int i = 0; i < head; i++) ops.Add((DiffKind.Same, i, i));
        Middle(a, b, head, a.Length - tail, head, b.Length - tail, ops);
        for (int i = 0; i < tail; i++) ops.Add((DiffKind.Same, a.Length - tail + i, b.Length - tail + i));

        var result = new List<DiffLine>(ops.Count);
        foreach (var (k, ai, bi) in ops)
            result.Add(k switch
            {
                DiffKind.Same => new DiffLine(k, b[bi], ai + 1, bi + 1),
                DiffKind.Removed => new DiffLine(k, a[ai], ai + 1, 0),
                _ => new DiffLine(k, b[bi], 0, bi + 1),
            });
        MarkChangedChars(result);
        return result;
    }

    static string[] Split(string? s) =>
        string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');

    /// Longest-common-subsequence diff of the differing middle; falls back to "all removed, all added" when huge.
    static void Middle(string[] a, string[] b, int a0, int a1, int b0, int b1, List<(DiffKind, int, int)> ops)
    {
        int n = a1 - a0, m = b1 - b0;
        if ((long)n * m > 4_000_000)
        {
            for (int i = a0; i < a1; i++) ops.Add((DiffKind.Removed, i, -1));
            for (int j = b0; j < b1; j++) ops.Add((DiffKind.Added, -1, j));
            return;
        }
        var L = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                L[i, j] = a[a0 + i] == b[b0 + j] ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[a0 + x] == b[b0 + y]) { ops.Add((DiffKind.Same, a0 + x, b0 + y)); x++; y++; }
            else if (y < m && (x == n || L[x, y + 1] >= L[x + 1, y])) { ops.Add((DiffKind.Added, -1, b0 + y)); y++; }
            else { ops.Add((DiffKind.Removed, a0 + x, -1)); x++; }
        }
        // show removals before their additions within each changed block
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Item1 != DiffKind.Added) continue;
            int j = i;
            while (j < ops.Count && ops[j].Item1 == DiffKind.Added) j++;
            int k = j;
            while (k < ops.Count && ops[k].Item1 == DiffKind.Removed) k++;
            if (k > j)
            {
                var added = ops.GetRange(i, j - i);
                var removed = ops.GetRange(j, k - j);
                ops.RemoveRange(i, k - i);
                ops.InsertRange(i, removed);
                ops.InsertRange(i + removed.Count, added);
                i = k - 1;
            }
            else i = j - 1;
        }
    }

    /// In blocks of removed lines followed by added lines, pair them up and mark the differing characters.
    static void MarkChangedChars(List<DiffLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != DiffKind.Removed) continue;
            int r0 = i;
            while (i < lines.Count && lines[i].Kind == DiffKind.Removed) i++;
            int a0 = i;
            while (i < lines.Count && lines[i].Kind == DiffKind.Added) i++;
            int pairs = Math.Min(a0 - r0, i - a0);
            for (int p = 0; p < pairs; p++)
            {
                string o = lines[r0 + p].Text, n = lines[a0 + p].Text;
                int pre = 0;
                while (pre < o.Length && pre < n.Length && o[pre] == n[pre]) pre++;
                int suf = 0;
                while (suf < o.Length - pre && suf < n.Length - pre && o[^(suf + 1)] == n[^(suf + 1)]) suf++;
                // only worth marking when the lines are mostly alike
                if (pre + suf < Math.Max(o.Length, n.Length) / 3) continue;
                lines[r0 + p] = lines[r0 + p] with { Start = pre, Length = o.Length - pre - suf };
                lines[a0 + p] = lines[a0 + p] with { Start = pre, Length = n.Length - pre - suf };
            }
            i--;
        }
    }
}
