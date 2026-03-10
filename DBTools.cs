using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DefaultNamespace;

[McpServerToolType]
public class DBTools
{
    // Attributes: McpServerTool registers this method as a callable tool that the llm can invoke.
    // Descriptions, tells the llm what the tool does. The llm read this to decide when to use it.
    [McpServerTool, Description("Read a source file when you need implementation details, surrounding code, or exact lines before making a change. Supports optional line ranges to reduce token use.")]
    
    // The Method Signature. 3 Parameters each with descriptions so the llm knows what to pass. 
   
    public static string ReadFile(
        [Description("Path to file")] string path,
        //The ? makes them nullable, and = null gives them default values so the llm does not have to specify.
        [Description("Start line (1-indexed, optional)")] int? startline = null,
        [Description("End line (optional)")] int? endline = null) 
    {
        // Reads the entire file into a string array, one element per line.
        var lines = File.ReadAllLines(path);

        // If either line parameter was provided.
        if (startline.HasValue || endline.HasValue)
        {
            int start = (startline ?? 1) - 1;
            int end = endline ?? lines.Length;
            lines = lines.Skip(start).Take(end - start).ToArray();
        }
        
        return string.Join("\n", lines);
    }

        // Attributes: McpServerTool registers this as a callable tool.
    // Description explains that this tool helps locate exact lines before reading.
    [McpServerTool, Description("Finds occurrences of a query in a single file and returns line numbers with small snippets.")]
    
    // The Method Signature. Parameters are described so the LLM knows how to use it.
    public static string FindInFile(
        [Description("Path to file")] string path,

        [Description("Search query (case-insensitive)")] string query,

        [Description("Max number of matches to return")] int maxMatches = 5,

        [Description("Max snippet characters to return per match")] int snippetChars = 160)
    {
        if (!File.Exists(path))
            return $"File not found: {path}";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        // Clamp values so they stay within reasonable limits.
        maxMatches = Math.Clamp(maxMatches, 1, 50);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var results = new List<object>();
        int lineNo = 0;

        // Read the file line-by-line instead of loading everything at once.
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;

            // Case-insensitive search.
            if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var snippet = line.Trim();

                // Trim snippet length to avoid large token output.
                if (snippet.Length > snippetChars)
                    snippet = snippet[..snippetChars] + "...";

                results.Add(new
                {
                    Line = lineNo,
                    Snippet = snippet
                });

                // Stop once we reach the maximum allowed matches.
                if (results.Count >= maxMatches)
                    break;
            }
        }

        // Return structured JSON so the LLM can easily parse it.
        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }



    // Attributes: Registers tool callable by the LLM.
    // Description tells the LLM that this searches across multiple files.
    [McpServerTool, Description("Search the codebase for symbols, strings, method names, routes, configuration keys, or error text when locating where something is defined or used.")]
    
    // The Method Signature. Parameters are described for LLM clarity.
    public static string ProjectSearch(
        [Description("Root directory to search under")] string rootDir,

        [Description("Query string to search for (case-insensitive)")] string query,

        [Description("Max number of results to return")] int limit = 10,

        [Description("Only search files with this extension (for example .cs)")] string extension = ".cs",

        [Description("Max snippet characters per result")] int snippetChars = 160)
    {
        if (!Directory.Exists(rootDir))
            return "Root directory not found.";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        limit = Math.Clamp(limit, 1, 50);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var results = new List<object>();

        // Search all files under the root directory.
        foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
        {
            // Skip common build/system folders.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;

            // Only search files with the specified extension.
            if (!Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
                continue;

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

                        if (results.Count >= limit)
                            break;
                    }
                }
            }
            catch
            {
                // If a file cannot be read, skip it.
            }

            if (results.Count >= limit)
                break;
        }

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
    if (!Directory.Exists(rootDir))
        return "Root directory not found.";

    maxDepth = Math.Clamp(maxDepth, 1, 10);
    maxEntries = Math.Clamp(maxEntries, 10, 1000);

    var results = new List<string>();
    var rootFull = Path.GetFullPath(rootDir);

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
    return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
}

[McpServerTool, Description("Searches across the codebase for text, symbols, method names, routes, config keys, or error messages.")]
public static string SearchCode(
    [Description("Root directory to search under")] string rootDir,
    [Description("Query text to search for (case-insensitive)")] string query,
    [Description("File extensions to include, for example .cs,.json,.csproj")] string extensions = ".cs,.json,.csproj",
    [Description("Maximum number of matches to return")] int limit = 20,
    [Description("Maximum snippet characters per result")] int snippetChars = 160)
{
    if (!Directory.Exists(rootDir))
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

    foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
        if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
        if (file.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}")) continue;

        if (!allowedExtensions.Contains(Path.GetExtension(file)))
            continue;

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

                    if (results.Count >= limit)
                        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                }
            }
        }
        catch
        {
        }
    }

    return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
}

[McpServerTool, Description("Reads a file and returns the most relevant surrounding lines around a symbol, method, class, or query match.")]
public static string ReadRelevantContext(
    [Description("Path to file")] string path,
    [Description("Symbol name or query to locate")] string query,
    [Description("Number of lines of context before the match")] int before = 20,
    [Description("Number of lines of context after the match")] int after = 40)
{
    if (!File.Exists(path))
        return $"File not found: {path}";

    if (string.IsNullOrWhiteSpace(query))
        return "Query was empty.";

    before = Math.Clamp(before, 0, 100);
    after = Math.Clamp(after, 0, 200);

    var lines = File.ReadAllLines(path);

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

            return JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }

    return $"No match found for '{query}' in {path}.";
}

[McpServerTool, Description("Summarizes a source file by listing namespaces, classes, interfaces, and method signatures so the model can inspect structure with fewer tokens.")]
public static string SummarizeCSharpFile(
    [Description("Path to .cs file")] string path)
{
    if (!File.Exists(path))
        return $"File not found: {path}";

    var lines = File.ReadAllLines(path);
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
                 && trimmed.Contains("(") && trimmed.Contains(")") && trimmed.EndsWith("{"))
            summary.Add(trimmed);
    }

    return JsonSerializer.Serialize(summary.Distinct().ToList(), new JsonSerializerOptions
    {
        WriteIndented = true
    });
}

}
