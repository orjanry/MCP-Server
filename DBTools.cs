using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace DefaultNamespace;

// ═══════════════════════════════════════════════════════════════════════════
//  DBTools — MCP server for LLM-assisted development workflows
//
//  Design goals:
//    1. Cover the core implementation loop: read → discover → edit
//    2. Minimal, orthogonal tool set (no overlapping tools)
//    3. Workspace-scoped safety on all file operations
//    4. Clear, directive descriptions that guide the LLM toward correct usage
//
//  Notes:
//    - BatchRead takes string[] (not a JSON-string blob) to avoid OpenAPI
//      adapter validation errors when wrapped by Open WebUI. The previous
//      string-blob approach failed with 422 because the adapter rejected
//      array inputs before they reached the C# method.
//    - Build/Test verification tools are intentionally excluded from this
//      configuration to isolate the effect of edit tools (Replace, WriteFile)
//      on LLM behavior relative to a read-only baseline.
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public class DBTools
{
    // ─── CONFIGURE THIS ───
    private static readonly string WS = "/workspace";
    private static readonly string LogPath = "/workspace/mcp_file_access.log";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // ───────────────────────────────────────────────────────────────────
    //  Internal helpers
    // ───────────────────────────────────────────────────────────────────

    private static string Resolve(string p)
    {
        if (string.IsNullOrWhiteSpace(p))
            throw new ArgumentException("Path was empty.", nameof(p));
        return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(WS, p));
    }

    private static bool InWorkspace(string p)
    {
        var full = Path.GetFullPath(p);
        var root = Path.GetFullPath(WS).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string text) =>
    string.Join("\n", text.Replace("\r\n", "\n").Replace("\r", "\n")
        .Split('\n').Select(l => l.TrimEnd()));
    private static void Log(string tool, string path, string detail = "")
    {
        try
        {
            var line = JsonSerializer.Serialize(
                new { ts = DateTime.UtcNow.ToString("o"), t = tool, p = path, d = detail },
                Compact);
            File.AppendAllText(LogPath, line + "\n");
        }
        catch { }
    }

    private static string ValidateFile(string path, out string resolved)
    {
        resolved = "";
        try { resolved = Resolve(path); }
        catch (Exception ex) { return $"Bad path: {ex.Message}"; }
        if (!InWorkspace(resolved)) return "Denied: outside workspace.";
        return "";
    }

    private static string ValidateDir(string path, out string resolved)
    {
        resolved = "";
        try { resolved = Resolve(path); }
        catch (Exception ex) { return $"Bad path: {ex.Message}"; }
        if (!InWorkspace(resolved)) return "Denied: outside workspace.";
        return "";
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", "node_modules", "dist", "build", "bakes" };

    private static bool Skip(string p)
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        return SkipDirs.Any(d => p.Contains($"{sep}{d}{sep}", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumFiles(string root, HashSet<string>? exts = null)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (Skip(f)) continue;
            if (exts is not null && !exts.Contains(Path.GetExtension(f))) continue;
            yield return f;
        }
    }

    private static string Snip(string line, int max)
    {
        var s = line.Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static HashSet<string> ParseExts(string exts)
        => exts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsWholeWord(string text, string word)
    {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_';
            bool rightOk = idx + word.Length >= text.Length
                           || !char.IsLetterOrDigit(text[idx + word.Length]) && text[idx + word.Length] != '_';
            if (leftOk && rightOk) return true;
            idx += word.Length;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //                         READ — file inspection
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Read a file and return its complete contents. " +
        "This is the primary tool for understanding existing code. " +
        "ALWAYS read relevant files before generating code that needs to match existing patterns. " +
        "Returns full source including all properties, methods, attributes, and namespaces. " +
        "For files over ~400 lines, consider using Outline first, then Read with a line range.")]
    public static string Read(
        [Description("File path relative to workspace root, e.g. src/Models/User.cs")] string path,
        [Description("Optional start line (1-indexed, 0 = from start)")] int from = 0,
        [Description("Optional end line (0 = to end)")] int to = 0)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";

        try
        {
            var lines = File.ReadAllLines(resolved);
            int s = from <= 0 ? 1 : from;
            int e = to <= 0 ? lines.Length : Math.Min(lines.Length, to);
            if (s > e) return "Bad range.";

            Log("Read", resolved, $"{s}-{e}");
            return string.Join("\n", lines.Skip(s - 1).Take(e - s + 1));
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description(
        "Read 2-10 files in a single call. Pass an array of file paths. " +
        "Use this when you know the set of files you need up front — it is cheaper than calling Read repeatedly. " +
        "Example paths: [\"src/Models/User.cs\", \"src/Data/UserConfiguration.cs\"]. " +
        "Returns each file's contents prefixed with '=== <path> ==='.")]
    public static string BatchRead(
        [Description("Array of file paths relative to workspace root")] string[] paths)
    {
        if (paths is null || paths.Length == 0) return "No paths provided.";
        if (paths.Length > 10) return "Max 10 files per batch.";

        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            sb.AppendLine($"=== {p} ===");
            sb.AppendLine(Read(p));
            sb.AppendLine();
        }
        Log("BatchRead", string.Join(",", paths), $"count={paths.Length}");
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Structural summary of a .cs file: class names, properties, method signatures, attributes. " +
        "Method bodies and comments are stripped. " +
        "Use this to preview a large file's shape before deciding whether to Read it in full. " +
        "WARNING: Outline is NOT sufficient when writing code that must match existing implementations — " +
        "always Read the actual file before generating code that depends on its internals.")]
    public static string Outline(
        [Description("Path to .cs file")] string path,
        [Description("Max chars per line")] int maxLineLen = 120)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";

        maxLineLen = Math.Clamp(maxLineLen, 60, 300);

        try
        {
            var lines = File.ReadAllLines(resolved);
            var sb = new StringBuilder();
            bool inBlockComment = false;

            foreach (var line in lines)
            {
                var t = line.Trim();

                if (t.StartsWith("/*")) inBlockComment = true;
                if (inBlockComment)
                {
                    if (t.Contains("*/")) inBlockComment = false;
                    continue;
                }
                if (t.StartsWith("//")) continue;

                bool keep = false;

                if (t.StartsWith("using ") && !t.Contains("(") && t.EndsWith(";"))
                    keep = true;
                else if (t.StartsWith("namespace "))
                    keep = true;
                else if (t.Contains(" class ") || t.Contains(" interface ") || t.Contains(" enum ")
                         || t.Contains(" record ") || t.Contains(" struct "))
                    keep = true;
                else if (t.StartsWith("[") && !t.StartsWith("[assembly"))
                    keep = true;
                else if (t.Contains("{ get;") || t.Contains("{ set;") || t.Contains("=> Set<"))
                    keep = true;
                else if ((t.StartsWith("public ") || t.StartsWith("private ")
                          || t.StartsWith("protected ") || t.StartsWith("internal "))
                         && !t.Contains("(") && t.EndsWith(";")
                         && !t.Contains(" class ") && !t.Contains("using ") && !t.Contains("return "))
                    keep = true;
                else if ((t.StartsWith("public ") || t.StartsWith("private ")
                          || t.StartsWith("protected ") || t.StartsWith("internal ")
                          || t.StartsWith("static ") || t.StartsWith("async ")
                          || t.StartsWith("override ") || t.StartsWith("virtual ")
                          || t.StartsWith("abstract "))
                         && t.Contains("("))
                    keep = true;

                if (!keep) continue;

                var entry = t.Length > maxLineLen ? t[..maxLineLen] + "…" : t;
                sb.AppendLine(entry);
            }

            Log("Outline", resolved, "");
            return sb.Length == 0 ? "No structural elements found." : sb.ToString();
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }


    // ═══════════════════════════════════════════════════════════════════
    //                      DISCOVER — find what exists
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "List files and folders in a directory. Use this to discover what exists in the codebase " +
        "when you don't yet know which files are relevant. After locating files with Tree, " +
        "use Read to get their contents.")]
    public static string Tree(
        [Description("Root dir, e.g. src/Models")] string root,
        [Description("Max depth (1-8)")] int depth = 2,
        [Description("Max entries returned")] int max = 80,
        [Description("Filter by extensions (empty = all), e.g. .cs,.json")] string exts = "")
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";

        depth = Math.Clamp(depth, 1, 8);
        max = Math.Clamp(max, 10, 500);

        var allowedExts = string.IsNullOrWhiteSpace(exts) ? null : ParseExts(exts);
        var rootFull = Path.GetFullPath(resolved);
        var results = new List<string>();

        void Walk(string dir, int d)
        {
            if (d > depth || results.Count >= max) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(x => x))
                {
                    var name = Path.GetFileName(sub);
                    if (SkipDirs.Contains(name)) continue;
                    if (results.Count >= max) return;
                    results.Add(Path.GetRelativePath(rootFull, sub) + "/");
                    Walk(sub, d + 1);
                }
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x))
                {
                    if (results.Count >= max) return;
                    if (allowedExts is not null && !allowedExts.Contains(Path.GetExtension(f))) continue;
                    results.Add(Path.GetRelativePath(rootFull, f));
                }
            }
            catch { }
        }

        Walk(rootFull, 1);
        Log("Tree", resolved, $"depth={depth};entries={results.Count}");
        return string.Join("\n", results);
    }

    [McpServerTool, Description(
        "Search for text across files in a directory. Returns file paths, line numbers, and short snippets. " +
        "IMPORTANT: Search results are pointers only — always Read the files found to get full context " +
        "before writing code that depends on them.")]
    public static string Search(
        [Description("Root dir to search within, e.g. src")] string root,
        [Description("Text to search for (case-insensitive)")] string query,
        [Description("Extensions filter, e.g. .cs,.cshtml,.json")] string exts = ".cs,.cshtml,.json,.razor,.csproj",
        [Description("Max results (1-60)")] int limit = 12,
        [Description("Snippet length in chars")] int snipLen = 120)
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";
        if (string.IsNullOrWhiteSpace(query)) return "Empty query.";

        limit = Math.Clamp(limit, 1, 60);
        snipLen = Math.Clamp(snipLen, 40, 300);

        var allowedExts = ParseExts(exts);
        var results = new List<object>();

        foreach (var file in EnumFiles(resolved, allowedExts))
        {
            int lineNo = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    results.Add(new
                    {
                        f = Path.GetRelativePath(WS, file),
                        l = lineNo,
                        s = Snip(line, snipLen)
                    });
                    break; // one hit per file — Read the file for more
                }
            }
            catch { }
            if (results.Count >= limit) break;
        }

        Log("Search", resolved, $"q={query};hits={results.Count}");
        return JsonSerializer.Serialize(results, Compact);
    }

    [McpServerTool, Description(
        "Find all places that reference a symbol (whole-word match). " +
        "Use this before refactoring a method, property, or type to find every call site. " +
        "Returns file paths with line numbers and snippets; use Read to examine each usage in context.")]
    public static string Usages(
        [Description("Root dir, e.g. src")] string root,
        [Description("Symbol name (exact whole-word match)")] string symbol,
        [Description("Extensions filter")] string exts = ".cs,.cshtml,.razor",
        [Description("Max results")] int limit = 20)
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";
        if (string.IsNullOrWhiteSpace(symbol)) return "Empty symbol.";

        limit = Math.Clamp(limit, 1, 50);
        var allowedExts = ParseExts(exts);
        var fileHits = new Dictionary<string, List<object>>();
        int total = 0;

        foreach (var file in EnumFiles(resolved, allowedExts))
        {
            int lineNo = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (!ContainsWholeWord(line, symbol)) continue;

                    var rel = Path.GetRelativePath(WS, file);
                    if (!fileHits.ContainsKey(rel))
                        fileHits[rel] = new List<object>();

                    if (fileHits[rel].Count < 3)
                    {
                        fileHits[rel].Add(new { l = lineNo, s = Snip(line, 100) });
                        total++;
                    }
                }
            }
            catch { }
            if (total >= limit) break;
        }

        var result = fileHits.Select(kv => new { f = kv.Key, hits = kv.Value }).ToList();
        Log("Usages", resolved, $"sym={symbol};files={fileHits.Count}");
        return JsonSerializer.Serialize(result, Compact);
    }


    // ═══════════════════════════════════════════════════════════════════
    //                          EDIT — modify files
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Replace an exact text substring in a file. " +
        "Use for small, surgical edits where you can identify a unique block of text to change. " +
        "The old text must match exactly (whitespace-sensitive). Returns 'Done.' on success. " +
        "For creating new files or rewriting large portions, use WriteFile instead.")]
    public static string Replace(
        [Description("File path")] string path,
        [Description("Exact old text to find (must be unique in the file)")] string old,
        [Description("New text to replace with")] string @new)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";
        if (string.IsNullOrEmpty(old)) return "Old text empty.";

        try
        {
            var content = Norm(File.ReadAllText(resolved));
            var normOld = Norm(old);

            var firstIdx = content.IndexOf(normOld, StringComparison.Ordinal);
            if (firstIdx < 0) return "Old text not found.";

            var secondIdx = content.IndexOf(normOld, firstIdx + 1, StringComparison.Ordinal);
            if (secondIdx >= 0)
                return "Old text is not unique in the file. Add more surrounding context to disambiguate.";

            var updated = content[..firstIdx] + Norm(@new) + content[(firstIdx + normOld.Length)..];
            File.WriteAllText(resolved, updated.Replace("\n", Environment.NewLine));

            Log("Replace", resolved, $"oldLen={old.Length};newLen={@new.Length}");
            return "Done.";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description(
        "Create a new file or completely overwrite an existing one with the provided contents. " +
        "Use this to create new files (e.g. a new model, a new test) or when rewriting most of a file. " +
        "For small targeted edits to existing files, use Replace instead — it's safer. " +
        "WARNING: overwrites existing file contents without confirmation. The file must be inside the workspace.")]
    public static string WriteFile(
        [Description("File path relative to workspace root, e.g. src/Models/Wishlist.cs")] string path,
        [Description("Complete file contents")] string content)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;

        try
        {
            var linkInfo = new FileInfo(resolved);
            if (linkInfo.Exists && (linkInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return "Denied: target is a symlink.";

            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                if (!InWorkspace(dir)) return "Denied: parent directory outside workspace.";
                Directory.CreateDirectory(dir);
            }

            var existed = File.Exists(resolved);
            File.WriteAllText(resolved, Norm(content).Replace("\n", Environment.NewLine));

            Log("WriteFile", resolved, $"bytes={content.Length};existed={existed}");
            return existed ? $"Overwrote {Path.GetRelativePath(WS, resolved)} ({content.Length} chars)."
                           : $"Created {Path.GetRelativePath(WS, resolved)} ({content.Length} chars).";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }
}