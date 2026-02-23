using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DefaultNamespace;

[McpServerToolType]
public class DBTools
{

    [McpServerTool, Description("Execute a read-only SQL query against the local SQLite database")]
    public static string QueryDatabase(
        [Description("SQL SELECT query to execute")] string sql)
    {
        // Safety: only allow SELECT queries
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return "Only SELECT queries are allowed.";
        }

        // Path to the SQLite database file. Adjust as needed.
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "app.db");
        var results = new List<Dictionary<string, object>>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var row = new Dictionary<string, object>();

            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.GetValue(i);
            }

            results.Add(row);
        }

        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

}

[McpServerToolType]
public class FileTools
{
    // Attributes: McpServerTool registers this method as a callable tool that the llm can invoke.
    // Descriptions, tells the llm what the tool does. The llm read this to decide when to use it.
    [McpServerTool, Description("Reads a file, optionally returning only spesific line ranges")]
    
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
    
    // Searches inside one file and returns line numbers with small snippets.
    [McpServerTool, Description("Finds occurrences of a query in a file and returns line numbers with small snippets.")]
    public static string FindInFile(
        [Description("Path to file")] string path,
        [Description("Search query (case-insensitive)")] string query,
        [Description("Max number of matches to return")] int maxMatches = 5,
        [Description("Max snippet characters per match")] int snippetChars = 160)
    {
        if (!File.Exists(path))
            return $"File not found: {path}";

        if (string.IsNullOrWhiteSpace(query))
            return "Query was empty.";

        maxMatches = Math.Clamp(maxMatches, 1, 50);
        snippetChars = Math.Clamp(snippetChars, 40, 400);

        var results = new List<object>();
        int lineNo = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNo++;

            if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var snippet = line.Trim();
                if (snippet.Length > snippetChars)
                    snippet = snippet[..snippetChars] + "...";

                results.Add(new { Line = lineNo, Snippet = snippet });

                if (results.Count >= maxMatches)
                    break;
            }
        }

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

        // Searches across multiple files and returns file path + line number + snippet.
    // Used to locate the correct file before calling ReadFile.
    [McpServerTool, Description("Searches across project files and returns file path, line number, and small snippet.")]
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

        foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;

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

                        results.Add(new { File = file, Line = lineNo, Snippet = snippet });

                        if (results.Count >= limit)
                            break;
                    }
                }
            }
            catch { }

            if (results.Count >= limit)
                break;
        }

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }
}