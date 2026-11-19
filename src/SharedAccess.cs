using System;
using System.Collections.Generic;
using System.Linq;

namespace Assist;

/// A session's shared paths list holds plain paths (read-only access) and "edit:"-prefixed paths
/// (the user allowed the agent to change files there).
public static class SharedAccess
{
    public const string EditPrefix = "edit:";

    public static (List<string> Read, List<string> Edit) Split(IEnumerable<string> shared)
    {
        var read = new List<string>(); var edit = new List<string>();
        foreach (var s in shared)
            if (s.StartsWith(EditPrefix, StringComparison.OrdinalIgnoreCase)) edit.Add(s[EditPrefix.Length..]);
            else read.Add(s);
        return (read, edit);
    }

    public static bool CanEdit(IEnumerable<string> shared, string path) =>
        Split(shared).Edit.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool CanRead(IEnumerable<string> shared, string path)
    {
        var (r, e) = Split(shared);
        return r.Concat(e).Any(x => path.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }
}
