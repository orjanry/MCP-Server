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
}