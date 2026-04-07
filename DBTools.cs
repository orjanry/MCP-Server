using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace DefaultNamespace;

[McpServerToolType]
public class DBTools
{
    // ─── CONFIGURE THIS ───
    // Set to your workspace root. All relative paths resolve from here.
    // Examples:
    //   Windows: @"C:\Users\Nodel\Bachelor"
    //   Linux/WSL: "/workspace"
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

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static void Log(string tool, string path, string detail = "")
    {
        try
        {
            var line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow.ToString("o"), t = tool, p = path, d = detail }, Compact);
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
            bool rightOk = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]) && text[idx + word.Length] != '_';
            if (leftOk && rightOk) return true;
            idx += word.Length;
        }
        return false;
    }

    /// <summary>
    /// Safely deserialize a BatchRead request from either a JSON string or a raw JsonElement array.
    /// The MCP framework may pass the parameter as a native JSON array (JsonElement) instead of a string.
    /// </summary>
    private static List<BatchReadRequest>? ParseBatchRequest(string raw)
    {
        // The input might be a JSON string, or it might be the raw JSON already.
        // Try direct deserialization first.
        try
        {
            return JsonSerializer.Deserialize<List<BatchReadRequest>>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Logging / experiment tools
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Clear the MCP access log.")]
    public static string ClearAccessLog()
    {
        try { File.WriteAllText(LogPath, ""); return "Cleared."; }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Read the MCP access log.")]
    public static string GetAccessLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return "No log yet.";
            return File.ReadAllText(LogPath);
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description("List unique file paths accessed via MCP (for baseline comparison).")]
    public static string GetAccessedFiles()
    {
        try
        {
            if (!File.Exists(LogPath)) return "No log yet.";

            var readTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Read", "Search", "Find", "Context", "Summarize", "BatchRead", "Outline" };

            var files = File.ReadLines(LogPath)
                .Select(line =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        var tool = r.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "";
                        var path = r.TryGetProperty("p", out var p) ? p.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(path)) return null;
                        if (readTools.Any(rt => tool.Contains(rt, StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
                            return path;
                        return null;
                    }
                    catch { return null; }
                })
                .Where(p => p is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();

            return JsonSerializer.Serialize(files, Compact);
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Estimate token count for files. Use after a task to compare MCP tokens vs pasting whole files.")]
    public static string EstimateTokens(
        [Description("Comma-separated file paths")] string paths)
    {
        var results = new List<object>();
        long totalChars = 0;
        long totalTokensEst = 0;

        foreach (var raw in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var err = ValidateFile(raw, out var resolved);
            if (!string.IsNullOrEmpty(err)) { results.Add(new { f = raw, err }); continue; }
            if (!File.Exists(resolved)) { results.Add(new { f = raw, err = "not found" }); continue; }

            try
            {
                var chars = new FileInfo(resolved).Length;
                var tokEst = (long)(chars / 3.5);
                totalChars += chars;
                totalTokensEst += tokEst;
                results.Add(new { f = Path.GetRelativePath(WS, resolved), chars, tok = tokEst });
            }
            catch (Exception ex) { results.Add(new { f = raw, err = ex.Message }); }
        }

        results.Add(new { f = "TOTAL", chars = totalChars, tok = totalTokensEst });
        Log("EstimateTokens", string.Join(",", paths), $"totalTok≈{totalTokensEst}");
        return JsonSerializer.Serialize(results, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  PRIMARY FILE READING
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Read a file and return its complete contents. " +
        "This is the most important tool — ALWAYS read files before generating code. " +
        "Returns full source including all properties, methods, attributes, and namespaces. " +
        "Use this for any file under 200 lines. For multiple files, call Read once per file.")]
    public static string Read(
        [Description("File path relative to workspace root, e.g. eShopOnWeb/src/ApplicationCore/Entities/CatalogItem.cs")] string path,
        [Description("Optional start line (1-indexed)")] int? from = null,
        [Description("Optional end line")] int? to = null)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";

        try
        {
            var lines = File.ReadAllLines(resolved);
            int s = Math.Max(1, from ?? 1);
            int e = Math.Min(lines.Length, to ?? lines.Length);
            if (s > e) return "Bad range.";

            Log("Read", resolved, $"{s}-{e}");
            return string.Join("\n", lines.Skip(s - 1).Take(e - s + 1));
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description(
        "Read 2-10 files in one call. Input is a JSON array as a STRING. " +
        "Example: [{\"p\":\"eShopOnWeb/src/ApplicationCore/Entities/CatalogItem.cs\"},{\"p\":\"eShopOnWeb/src/Infrastructure/Data/Config/CatalogItemConfiguration.cs\"}] " +
        "Each entry needs a \"p\" field with the file path. Optional \"from\" and \"to\" for line ranges. " +
        "IMPORTANT: The entire input must be a single JSON string, not separate arguments.")]
    public static string BatchRead(
        [Description("A JSON string containing an array of objects with p (path), optional from/to line numbers")] string requests)
    {
        // Handle case where MCP framework passes a JsonElement or already-parsed object
        string json;
        if (requests.TrimStart().StartsWith("["))
        {
            json = requests;
        }
        else
        {
            // Maybe it's been double-encoded or wrapped
            try
            {
                json = JsonSerializer.Deserialize<string>(requests) ?? requests;
            }
            catch
            {
                json = requests;
            }
        }

        List<BatchReadRequest>? items = ParseBatchRequest(json);
        if (items is null || items.Count == 0)
            return "Invalid JSON. Expected a JSON array string like: [{\"p\":\"path/to/file.cs\"},{\"p\":\"path/to/other.cs\"}]";
        if (items.Count > 10) return "Max 10 files per batch.";

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.AppendLine($"=== {item.P} ===");
            sb.AppendLine(Read(item.P, item.From, item.To));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private class BatchReadRequest
    {
        public string P { get; set; } = "";
        public int? From { get; set; }
        public int? To { get; set; }
    }

    [McpServerTool, Description("Read first N lines of a file. Use to preview large files before reading the whole thing.")]
    public static string Head(
        [Description("File path")] string path,
        [Description("Number of lines (default 50, max 400)")] int n = 50)
    {
        return Read(path, 1, Math.Clamp(n, 1, 400));
    }

    // ───────────────────────────────────────────────────────────────────
    //  SEARCH — find WHERE something is, then Read to get full content
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Search for text across the codebase. Returns file paths and short snippets. " +
        "IMPORTANT: Search results are only pointers — always call Read on the files found " +
        "to get the full content before writing any code.")]
    public static string Search(
        [Description("Root dir, e.g. eShopOnWeb/src/ApplicationCore")] string root,
        [Description("Text to search for (case-insensitive)")] string query,
        [Description("compact (1 hit/file) | detail (multi-hit) | files (paths only)")] string mode = "compact",
        [Description("Extensions filter, e.g. .cs,.json")] string exts = ".cs,.cshtml,.json,.razor,.csproj",
        [Description("Max results")] int limit = 12,
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
        int maxPerFile = mode == "detail" ? 5 : 1;
        bool fileOnly = mode == "files";

        foreach (var file in EnumFiles(resolved, allowedExts))
        {
            int lineNo = 0, hits = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var rel = Path.GetRelativePath(WS, file);
                    if (fileOnly)
                        results.Add(new { f = rel, l = lineNo });
                    else
                        results.Add(new { f = rel, l = lineNo, s = Snip(line, snipLen) });

                    Log("Search", file, $"q={query};l={lineNo}");
                    hits++;
                    if (hits >= maxPerFile) break;
                }
            }
            catch { }
            if (results.Count >= limit) break;
        }

        Log("Search", resolved, $"q={query};mode={mode};hits={results.Count}");
        return JsonSerializer.Serialize(results, Compact);
    }

    [McpServerTool, Description("Find text within a single file. Returns line numbers and snippets. Use Read to get full context.")]
    public static string FindInFile(
        [Description("File path")] string path,
        [Description("Text to find")] string query,
        [Description("Max matches")] int max = 5,
        [Description("Snippet length in chars")] int snipLen = 140)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";
        if (string.IsNullOrWhiteSpace(query)) return "Empty query.";

        max = Math.Clamp(max, 1, 50);
        snipLen = Math.Clamp(snipLen, 40, 300);
        var results = new List<object>();
        int lineNo = 0;

        try
        {
            foreach (var line in File.ReadLines(resolved))
            {
                lineNo++;
                if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(new { l = lineNo, s = Snip(line, snipLen) });
                    if (results.Count >= max) break;
                }
            }
            Log("FindInFile", resolved, $"q={query};hits={results.Count}");
            return JsonSerializer.Serialize(results, Compact);
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Regex search across files in a directory.")]
    public static string Grep(
        [Description("Root dir")] string root,
        [Description("Regex pattern")] string pattern,
        [Description("Extensions filter")] string exts = ".cs",
        [Description("Max results")] int limit = 15)
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";

        limit = Math.Clamp(limit, 1, 50);
        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { return $"Bad regex: {ex.Message}"; }

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
                    if (regex.IsMatch(line))
                    {
                        results.Add(new { f = Path.GetRelativePath(WS, file), l = lineNo, s = Snip(line, 120) });
                        Log("Grep", file, $"pat={pattern};l={lineNo}");
                        if (results.Count >= limit) break;
                    }
                }
            }
            catch { }
            if (results.Count >= limit) break;
        }

        Log("Grep", resolved, $"pat={pattern};hits={results.Count}");
        return JsonSerializer.Serialize(results, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Context reading
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Read lines around a search match in a file. Good for seeing context around a specific symbol or method.")]
    public static string Context(
        [Description("File path")] string path,
        [Description("Text to locate")] string query,
        [Description("Lines before match")] int before = 5,
        [Description("Lines after match")] int after = 20)
    {
        var err = ValidateFile(path, out var resolved);
        if (err != "") return err;
        if (!File.Exists(resolved)) return "Not found.";
        if (string.IsNullOrWhiteSpace(query)) return "Empty query.";

        before = Math.Clamp(before, 0, 60);
        after = Math.Clamp(after, 0, 100);

        try
        {
            var lines = File.ReadAllLines(resolved);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int s = Math.Max(0, i - before);
                int e = Math.Min(lines.Length - 1, i + after);

                var sb = new StringBuilder();
                for (int j = s; j <= e; j++)
                    sb.AppendLine($"{j + 1}|{lines[j]}");

                Log("Context", resolved, $"q={query};{s + 1}-{e + 1}");
                return sb.ToString();
            }
            return $"No match for '{query}'.";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Project tree
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "List files and folders in a directory tree. Use to discover what files exist. " +
        "After finding files with Tree, ALWAYS use Read to get their contents before writing code.")]
    public static string Tree(
        [Description("Root dir, e.g. eShopOnWeb/src/ApplicationCore")] string root,
        [Description("Max depth")] int depth = 2,
        [Description("Max entries")] int max = 80,
        [Description("Filter by extensions (empty=all), e.g. .cs,.json")] string exts = "")
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

    // ───────────────────────────────────────────────────────────────────
    //  OUTLINE — structural summary only
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Shows a structural summary of a .cs file: class names, properties, method signatures. " +
        "WARNING: This strips method bodies and comments — it is NOT sufficient for writing code " +
        "that must match existing patterns. Use Read instead when you need exact implementations.")]
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
            int braceDepth = 0;
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

                braceDepth += t.Count(c => c == '{') - t.Count(c => c == '}');

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
                else if ((t.StartsWith("public ") || t.StartsWith("private ") || t.StartsWith("protected ")
                          || t.StartsWith("internal "))
                         && !t.Contains("(") && t.EndsWith(";") && !t.Contains(" class ")
                         && !t.Contains("using ") && !t.Contains("return "))
                    keep = true;
                else if ((t.StartsWith("public ") || t.StartsWith("private ") || t.StartsWith("protected ")
                          || t.StartsWith("internal ") || t.StartsWith("static ") || t.StartsWith("async ")
                          || t.StartsWith("override ") || t.StartsWith("virtual ") || t.StartsWith("abstract "))
                         && t.Contains("("))
                    keep = true;

                if (!keep) continue;

                var entry = t.Length > maxLineLen ? t[..maxLineLen] + "…" : t;
                sb.AppendLine(entry);
            }

            Log("Outline", resolved, $"items={sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
            return sb.Length == 0 ? "No structural elements found." : sb.ToString();
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    // ───────────────────────────────────────────────────────────────────
    //  File stats
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Get file sizes and line counts. Helps decide whether to Read a full file or use Head.")]
    public static string FileStats(
        [Description("Comma-separated file paths, or a directory path")] string input,
        [Description("Extensions filter if input is a directory")] string exts = ".cs")
    {
        var paths = new List<string>();

        var dirErr = ValidateDir(input, out var resolvedDir);
        if (dirErr == "" && Directory.Exists(resolvedDir))
        {
            var allowedExts = ParseExts(exts);
            paths.AddRange(EnumFiles(resolvedDir, allowedExts).Take(50));
        }
        else
        {
            foreach (var raw in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fErr = ValidateFile(raw, out var resolved);
                if (fErr == "" && File.Exists(resolved)) paths.Add(resolved);
            }
        }

        var results = paths.Select(f =>
        {
            try
            {
                var info = new FileInfo(f);
                var lineCount = File.ReadLines(f).Count();
                return new { f = Path.GetRelativePath(WS, f), lines = lineCount, kb = Math.Round(info.Length / 1024.0, 1) };
            }
            catch { return new { f = Path.GetRelativePath(WS, f), lines = 0, kb = 0.0 }; }
        }).ToList();

        Log("FileStats", input, $"files={results.Count}");
        return JsonSerializer.Serialize(results, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  REFERENCES
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Find all files that reference a symbol (whole-word match). " +
        "Returns file paths and snippets — use Read to examine the actual code.")]
    public static string Usages(
        [Description("Root dir, e.g. eShopOnWeb/src")] string root,
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

                    Log("Usages", file, $"sym={symbol};l={lineNo}");
                }
            }
            catch { }
            if (total >= limit) break;
        }

        var result = fileHits.Select(kv => new { f = kv.Key, hits = kv.Value }).ToList();
        Log("Usages", resolved, $"sym={symbol};files={fileHits.Count}");
        return JsonSerializer.Serialize(result, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  TYPE HIERARCHY
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Find what a type inherits from and what derives from it.")]
    public static string Hierarchy(
        [Description("Root dir")] string root,
        [Description("Exact type name")] string typeName,
        [Description("Extensions filter")] string exts = ".cs")
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";
        if (string.IsNullOrWhiteSpace(typeName)) return "Empty type name.";

        var allowedExts = ParseExts(exts);
        string? baseType = null;
        var derived = new List<object>();

        foreach (var file in EnumFiles(resolved, allowedExts))
        {
            int lineNo = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    var t = line.Trim();
                    if (!t.Contains(":") || (!t.Contains(" class ") && !t.Contains(" interface ") && !t.Contains(" struct ")))
                        continue;

                    var colonIdx = t.IndexOf(':');
                    if (colonIdx < 0) continue;

                    var beforeColon = t[..colonIdx].Trim();
                    var afterColon = t[(colonIdx + 1)..].Trim().TrimEnd('{').Trim();

                    if (baseType is null && ContainsWholeWord(beforeColon, $"class {typeName}"))
                        baseType = afterColon;
                    else if (baseType is null && ContainsWholeWord(beforeColon, $"interface {typeName}"))
                        baseType = afterColon;
                    else if (ContainsWholeWord(afterColon, typeName))
                        derived.Add(new { f = Path.GetRelativePath(WS, file), l = lineNo, s = Snip(t, 120) });
                }
            }
            catch { }
        }

        var result = new { type = typeName, @base = baseType ?? "(none)", derived };
        Log("Hierarchy", resolved, $"type={typeName};derived={derived.Count}");
        return JsonSerializer.Serialize(result, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  DI scanner
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Find DI registrations and middleware mappings in ASP.NET projects.")]
    public static string DiScan(
        [Description("Root dir")] string root,
        [Description("Optional: filter to a specific service name")] string filter = "")
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";

        var patterns = new[] {
            "AddScoped", "AddTransient", "AddSingleton", "AddHostedService",
            "AddDbContext", "AddHttpClient", "AddControllers", "AddRazorPages",
            "AddSignalR", "AddAuthentication", "AddAuthorization",
            "UseMiddleware", "MapGet", "MapPost", "MapPut", "MapDelete",
            "MapControllers", "MapHub", "builder.Services", "app.Use"
        };

        var results = new List<object>();

        foreach (var file in EnumFiles(resolved, ParseExts(".cs")))
        {
            int lineNo = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    var t = line.Trim();
                    if (string.IsNullOrWhiteSpace(t) || t.StartsWith("//")) continue;

                    if (!patterns.Any(p => t.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrWhiteSpace(filter) && !t.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                    results.Add(new { f = Path.GetRelativePath(WS, file), l = lineNo, s = Snip(t, 140) });
                    if (results.Count >= 40) break;
                }
            }
            catch { }
            if (results.Count >= 40) break;
        }

        Log("DiScan", resolved, $"filter={filter};hits={results.Count}");
        return JsonSerializer.Serialize(results, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Edit tools
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Replace exact text in a file. Both old and new text should be provided as plain strings.")]
    public static string Replace(
        [Description("File path")] string path,
        [Description("Exact old text to find")] string old,
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
            var idx = content.IndexOf(normOld, StringComparison.Ordinal);
            if (idx < 0) return "Old text not found.";

            var updated = content[..idx] + Norm(@new) + content[(idx + normOld.Length)..];
            File.WriteAllText(resolved, updated.Replace("\n", Environment.NewLine));

            Log("Replace", resolved, $"oldLen={old.Length};newLen={@new.Length}");
            return "Done.";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }


    // ───────────────────────────────────────────────────────────────────
    //  Diff
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Line-by-line diff between two files.")]
    public static string Diff(
        [Description("Original file path")] string pathA,
        [Description("Modified file path")] string pathB,
        [Description("Context lines around changes")] int ctx = 2)
    {
        var errA = ValidateFile(pathA, out var rA);
        if (errA != "") return errA;
        var errB = ValidateFile(pathB, out var rB);
        if (errB != "") return errB;
        if (!File.Exists(rA)) return $"Not found: {pathA}";
        if (!File.Exists(rB)) return $"Not found: {pathB}";

        ctx = Math.Clamp(ctx, 0, 10);

        try
        {
            var lA = File.ReadAllLines(rA);
            var lB = File.ReadAllLines(rB);
            var sb = new StringBuilder();
            sb.AppendLine($"--- {Path.GetRelativePath(WS, rA)}");
            sb.AppendLine($"+++ {Path.GetRelativePath(WS, rB)}");

            int maxLen = Math.Max(lA.Length, lB.Length);
            var changes = new List<int>();
            for (int i = 0; i < maxLen; i++)
            {
                var a = i < lA.Length ? lA[i] : null;
                var b = i < lB.Length ? lB[i] : null;
                if (a != b) changes.Add(i);
            }

            if (changes.Count == 0) return "Identical.";

            var hunks = new List<(int s, int e)>();
            int hs = changes[0], he = changes[0];
            for (int i = 1; i < changes.Count; i++)
            {
                if (changes[i] - he <= ctx * 2 + 1) he = changes[i];
                else { hunks.Add((hs, he)); hs = changes[i]; he = changes[i]; }
            }
            hunks.Add((hs, he));

            foreach (var (start, end) in hunks.Take(20))
            {
                int s = Math.Max(0, start - ctx);
                int e = Math.Min(maxLen - 1, end + ctx);
                sb.AppendLine($"@@ {s + 1}-{e + 1} @@");
                for (int i = s; i <= e; i++)
                {
                    var a = i < lA.Length ? lA[i] : null;
                    var b = i < lB.Length ? lB[i] : null;
                    if (a == b) sb.AppendLine($" {a}");
                    else
                    {
                        if (a is not null) sb.AppendLine($"-{a}");
                        if (b is not null) sb.AppendLine($"+{b}");
                    }
                }
            }

            Log("Diff", rA, $"vs={rB};changes={changes.Count}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Project overview
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Get a project overview: file counts by type, total lines, key config files. Good for initial orientation.")]
    public static string ProjectInfo(
        [Description("Root dir, e.g. eShopOnWeb")] string root)
    {
        var err = ValidateDir(root, out var resolved);
        if (err != "") return err;
        if (!Directory.Exists(resolved)) return "Dir not found.";

        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var extLines = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var keyFiles = new List<string>();
        int totalFiles = 0;

        var keyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program.cs", "Startup.cs", "appsettings.json", "appsettings.Development.json",
            "launchSettings.json", "package.json", "tsconfig.json",
            "docker-compose.yml", "Dockerfile", "global.json", ".env"
        };

        var keyExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".csproj", ".sln", ".fsproj" };

        foreach (var file in EnumFiles(resolved))
        {
            totalFiles++;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "(none)";

            extCounts[ext] = extCounts.GetValueOrDefault(ext) + 1;
            try { extLines[ext] = extLines.GetValueOrDefault(ext) + File.ReadLines(file).Count(); }
            catch { }

            var name = Path.GetFileName(file);
            if (keyNames.Contains(name) || keyExts.Contains(ext))
                keyFiles.Add(Path.GetRelativePath(WS, file));
        }

        var result = new
        {
            root = Path.GetRelativePath(WS, resolved),
            files = totalFiles,
            types = extCounts.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new { ext = kv.Key, n = kv.Value, lines = extLines.GetValueOrDefault(kv.Key) }),
            key = keyFiles.Take(20)
        };

        Log("ProjectInfo", resolved, $"files={totalFiles}");
        return JsonSerializer.Serialize(result, Compact);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Comparison report
    // ───────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Generate MCP vs paste comparison data with fairness analysis.")]
    public static string ComparisonReport()
    {
        try
        {
            if (!File.Exists(LogPath)) return "No log yet.";

            var accessedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int toolCalls = 0;

            foreach (var line in File.ReadLines(LogPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var path = r.TryGetProperty("p", out var p) ? p.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        accessedFiles.Add(path);
                    toolCalls++;
                }
                catch { }
            }

            long baseChars = 0, baseLines = 0;
            var details = new List<object>();

            foreach (var f in accessedFiles.OrderBy(x => x))
            {
                try
                {
                    var info = new FileInfo(f);
                    var lc = File.ReadLines(f).Count();
                    baseChars += info.Length;
                    baseLines += lc;
                    details.Add(new { f = Path.GetRelativePath(WS, f), chars = info.Length, lines = lc, tok = (long)(info.Length / 3.5) });
                }
                catch { }
            }

            var report = new
            {
                mcp = new { calls = toolCalls, files = accessedFiles.Count },
                paste = new { total_chars = baseChars, total_lines = baseLines, tok_est = (long)(baseChars / 3.5), file_details = details }
            };

            return JsonSerializer.Serialize(report, Compact);
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }
}