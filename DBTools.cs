using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DefaultNamespace;

[McpServerToolType]
public class DBTools
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