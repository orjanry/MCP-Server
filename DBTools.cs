using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DefaultNamespace;

[McpServerToolType]
public class DBTools
{
    private static readonly string WorkspaceRoot = "/workspace";
    private static readonly string AccessLogPath = "/workspace/mcp_file_access.log";

    private static string ResolveWorkspacePath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Path was empty.", nameof(inputPath));

        if (Path.IsPathRooted(inputPath))
            return Path.GetFullPath(inputPath);

        return Path.GetFullPath(Path.Combine(WorkspaceRoot, inputPath));
    }

    private static bool IsPathInsideWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(WorkspaceRoot);

        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   fullPath.TrimEnd(Path.DirectorySeparatorChar),
                   fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n");
    }

    private static void LogAccess(string toolName, string path, string details = "")
    {
        try
        {
            var entry = new
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Tool = toolName,
                Path = path,
                Details = details
            };

            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            File.AppendAllText(AccessLogPath, line);
        }
        catch
        {
            // Logging should never break tool execution.
        }
    }

    private static string ValidateAndResolveFilePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        try
        {
            resolvedPath = ResolveWorkspacePath(path);
        }
        catch (Exception ex)
        {
            return $"Invalid path: {ex.Message}";
        }

        if (!IsPathInsideWorkspace(resolvedPath))
            return "Access denied: path is outside /workspace.";

        return string.Empty;
    }

    private static string ValidateAndResolveDirectoryPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        try
        {
            resolvedPath = ResolveWorkspacePath(path);
        }
        catch (Exception ex)
        {
            return $"Invalid root directory: {ex.Message}";
        }

        if (!IsPathInsideWorkspace(resolvedPath))
            return "Access denied: root directory is outside /workspace.";

        return string.Empty;
    }

    [McpServerTool, Description("Clears the MCP file access log before a new experiment run.")]
    public static string ClearAccessLog()
    {
        try
        {
            File.WriteAllText(AccessLogPath, string.Empty);
            return "Access log cleared.";
        }
        catch (Exception ex)
        {
            return $"Failed to clear access log: {ex.Message}";
        }
    }

    [McpServerTool, Description("Reads the MCP file access log so you can see exactly which files and tools were used in the last run.")]
    public static string GetAccessLog()
    {
        try
        {
            if (!File.Exists(AccessLogPath))
                return "Access log does not exist yet.";

            return File.ReadAllText(AccessLogPath);
        }
        catch (Exception ex)
        {
            return $"Failed to read access log: {ex.Message}";
        }
    }

    [McpServerTool, Description("Read a source file when you need implementation details, surrounding code, or exact lines before making a change. Supports optional line ranges to reduce token use.")]
    public static string ReadFile(
        [Description("Path to file under /workspace")] string path,
        [Description("Start line (1-indexed, optional)")] int? startline = null,
        [Description("End line (optional)")] int? endline = null)
    {
        var validation = ValidateAndResolveFilePath(path, out var resolvedPath);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!File.Exists(resolvedPath))
            return $"File not found: {resolvedPath}";

        try
        {
            var lines = File.ReadAllLines(resolvedPath);

            int start = Math.Max(1, startline ?? 1);
            int end = Math.Min(lines.Length, endline ?? lines.Length);

            if (start > end)
                return "Invalid line range.";

            var selected = lines.Skip(start - 1).Take(end - start + 1).ToArray();

            LogAccess("ReadFile", resolvedPath, $"lines={start}-{end}");

            return string.Join("\n", selected);
        }
        catch (Exception ex)
        {
            return $"Read failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Finds occurrences of a query in a single file and returns line numbers with small snippets.")]
    public static string FindInFile(
        [Description("Path to file under /workspace")] string path,
        [Description("Search query (case-insensitive)")] string query,
        [Description("Max number of matches to return")] int maxMatches = 5,
        [Description("Max snippet characters to return per match")] int snippetChars = 160)
    {
        var validation = ValidateAndResolveFilePath(path, out var resolvedPath);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!File.Exists(resolvedPath))
            return $"File not found: {resolvedPath}";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        maxMatches = Math.Clamp(maxMatches, 1, 50);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var results = new List<object>();
        int lineNo = 0;

        try
        {
            foreach (var line in File.ReadLines(resolvedPath))
            {
                lineNo++;

                if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var snippet = line.Trim();

                    if (snippet.Length > snippetChars)
                        snippet = snippet[..snippetChars] + "...";

                    results.Add(new
                    {
                        Line = lineNo,
                        Snippet = snippet
                    });

                    if (results.Count >= maxMatches)
                        break;
                }
            }

            LogAccess("FindInFile", resolvedPath, $"query={query};matches={results.Count}");

            return JsonSerializer.Serialize(results, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Search the codebase for symbols, strings, method names, routes, configuration keys, or error text when locating where something is defined or used.")]
    public static string ProjectSearch(
        [Description("Root directory to search under")] string rootDir,
        [Description("Query string to search for (case-insensitive)")] string query,
        [Description("Max number of results to return")] int limit = 10,
        [Description("Only search files with this extension (for example .cs)")] string extension = ".cs",
        [Description("Max snippet characters per result")] int snippetChars = 160)
    {
        var validation = ValidateAndResolveDirectoryPath(rootDir, out var resolvedRoot);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!Directory.Exists(resolvedRoot))
            return "Root directory not found.";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        limit = Math.Clamp(limit, 1, 50);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var results = new List<object>();

        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}")) continue;
            if (!Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;

            int lineNo = 0;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;

                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var snippet = line.Trim();

                        if (snippet.Length > snippetChars)
                            snippet = snippet[..snippetChars] + "...";

                        results.Add(new
                        {
                            File = file,
                            Line = lineNo,
                            Snippet = snippet
                        });

                        LogAccess("ProjectSearchHit", file, $"query={query};line={lineNo}");

                        if (results.Count >= limit)
                            break;
                    }
                }
            }
            catch
            {
                // Skip unreadable files.
            }

            if (results.Count >= limit)
                break;
        }

        LogAccess("ProjectSearch", resolvedRoot, $"query={query};extension={extension};results={results.Count}");

        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [McpServerTool, Description("Lists the project tree so the model can discover relevant files before reading or searching.")]
    public static string GetProjectTree(
        [Description("Root directory to inspect")] string rootDir,
        [Description("Maximum directory depth")] int maxDepth = 3,
        [Description("Maximum number of entries to return")] int maxEntries = 200)
    {
        var validation = ValidateAndResolveDirectoryPath(rootDir, out var resolvedRoot);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!Directory.Exists(resolvedRoot))
            return "Root directory not found.";

        maxDepth = Math.Clamp(maxDepth, 1, 10);
        maxEntries = Math.Clamp(maxEntries, 10, 1000);

        var results = new List<string>();
        var rootFull = Path.GetFullPath(resolvedRoot);

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth || results.Count >= maxEntries)
                return;

            IEnumerable<string> directories;
            IEnumerable<string> files;

            try
            {
                directories = Directory.EnumerateDirectories(dir)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name != "bin" && name != "obj" && name != ".git" && name != ".vs";
                    })
                    .OrderBy(d => d);

                files = Directory.EnumerateFiles(dir)
                    .OrderBy(f => f);
            }
            catch
            {
                return;
            }

            foreach (var subDir in directories)
            {
                if (results.Count >= maxEntries) return;
                results.Add(Path.GetRelativePath(rootFull, subDir) + "/");
                Walk(subDir, depth + 1);
            }

            foreach (var file in files)
            {
                if (results.Count >= maxEntries) return;
                results.Add(Path.GetRelativePath(rootFull, file));
            }
        }

        Walk(rootFull, 1);

        LogAccess("GetProjectTree", resolvedRoot, $"maxDepth={maxDepth};maxEntries={maxEntries}");

        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [McpServerTool, Description("Searches across the codebase for text, symbols, method names, routes, config keys, or error messages.")]
    public static string SearchCode(
        [Description("Root directory to search under")] string rootDir,
        [Description("Query text to search for (case-insensitive)")] string query,
        [Description("File extensions to include, for example .cs,.json,.csproj,.cshtml")] string extensions = ".cs,.json,.csproj,.cshtml,.razor",
        [Description("Maximum number of matches to return")] int limit = 20,
        [Description("Maximum snippet characters per result")] int snippetChars = 160)
    {
        var validation = ValidateAndResolveDirectoryPath(rootDir, out var resolvedRoot);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!Directory.Exists(resolvedRoot))
            return "Root directory not found.";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        limit = Math.Clamp(limit, 1, 100);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var allowedExtensions = extensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<object>();

        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}")) continue;
            if (!allowedExtensions.Contains(Path.GetExtension(file))) continue;

            int lineNo = 0;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;

                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var snippet = line.Trim();

                        if (snippet.Length > snippetChars)
                            snippet = snippet[..snippetChars] + "...";

                        results.Add(new
                        {
                            File = file,
                            Line = lineNo,
                            Snippet = snippet
                        });

                        LogAccess("SearchCodeHit", file, $"query={query};line={lineNo}");

                        if (results.Count >= limit)
                        {
                            LogAccess("SearchCode", resolvedRoot, $"query={query};extensions={extensions};results={results.Count}");
                            return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                        }
                    }
                }
            }
            catch
            {
                // Skip unreadable files.
            }
        }

        LogAccess("SearchCode", resolvedRoot, $"query={query};extensions={extensions};results={results.Count}");

        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [McpServerTool, Description("Reads a file and returns the most relevant surrounding lines around a symbol, method, class, or query match.")]
    public static string ReadRelevantContext(
        [Description("Path to file under /workspace")] string path,
        [Description("Symbol name or query to locate")] string query,
        [Description("Number of lines of context before the match")] int before = 20,
        [Description("Number of lines of context after the match")] int after = 40)
    {
        var validation = ValidateAndResolveFilePath(path, out var resolvedPath);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!File.Exists(resolvedPath))
            return $"File not found: {resolvedPath}";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        before = Math.Clamp(before, 0, 100);
        after = Math.Clamp(after, 0, 200);

        try
        {
            var lines = File.ReadAllLines(resolvedPath);

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int start = Math.Max(0, i - before);
                    int end = Math.Min(lines.Length - 1, i + after);

                    var output = new List<object>();
                    for (int j = start; j <= end; j++)
                    {
                        output.Add(new
                        {
                            Line = j + 1,
                            Text = lines[j]
                        });
                    }

                    LogAccess("ReadRelevantContext", resolvedPath, $"query={query};lines={start + 1}-{end + 1}");

                    return JsonSerializer.Serialize(output, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                }
            }

            return $"No match found for '{query}' in {resolvedPath}.";
        }
        catch (Exception ex)
        {
            return $"Context read failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Summarizes a C# source file by listing namespaces, classes, interfaces, and likely method signatures so the model can inspect structure with fewer tokens.")]
    public static string SummarizeCSharpFile(
        [Description("Path to .cs file under /workspace")] string path)
    {
        var validation = ValidateAndResolveFilePath(path, out var resolvedPath);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!File.Exists(resolvedPath))
            return $"File not found: {resolvedPath}";

        try
        {
            var lines = File.ReadAllLines(resolvedPath);
            var summary = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("namespace "))
                    summary.Add(trimmed);
                else if (trimmed.Contains(" class "))
                    summary.Add(trimmed);
                else if (trimmed.Contains(" interface "))
                    summary.Add(trimmed);
                else if ((trimmed.StartsWith("public ") || trimmed.StartsWith("private ") || trimmed.StartsWith("protected ") || trimmed.StartsWith("internal "))
                         && trimmed.Contains("(") && trimmed.Contains(")") && (trimmed.EndsWith("{") || trimmed.EndsWith(";")))
                    summary.Add(trimmed);
            }

            LogAccess("SummarizeCSharpFile", resolvedPath, $"summaryItems={summary.Count}");

            return JsonSerializer.Serialize(summary.Distinct().ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            return $"Summary failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Replace an exact block of text inside a file under /workspace. Use this for small code edits without rewriting the whole file.")]
    public static string ReplaceInFile(
        [Description("Path to file under /workspace")] string path,
        [Description("Exact text to replace")] string oldText,
        [Description("New text")] string newText)
    {
        var validation = ValidateAndResolveFilePath(path, out var resolvedPath);
        if (!string.IsNullOrEmpty(validation))
            return validation;

        if (!File.Exists(resolvedPath))
            return $"File not found: {resolvedPath}";

        if (string.IsNullOrEmpty(oldText))
            return "Old text cannot be empty.";

        try
        {
            var content = File.ReadAllText(resolvedPath);

            var normalizedContent = NormalizeLineEndings(content);
            var normalizedOldText = NormalizeLineEndings(oldText);
            var normalizedNewText = NormalizeLineEndings(newText);

            var index = normalizedContent.IndexOf(normalizedOldText, StringComparison.Ordinal);
            if (index < 0)
                return "Target text not found in file.";

            var updated =
                normalizedContent[..index] +
                normalizedNewText +
                normalizedContent[(index + normalizedOldText.Length)..];

            updated = updated.Replace("\n", Environment.NewLine);

            File.WriteAllText(resolvedPath, updated);

            LogAccess("ReplaceInFile", resolvedPath, $"oldLength={oldText.Length};newLength={newText.Length}");

            return "Replacement successful.";
        }
        catch (Exception ex)
        {
            return $"Write failed: {ex.Message}";
        }
    }
}