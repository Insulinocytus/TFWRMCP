using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

public sealed class Tool
{
    public string Name { get; }
    public string Description { get; }
    public JObject InputSchema { get; }

    /// <summary>Capabilities that must be available for the Tool to run.</summary>
    public string[] Capabilities { get; }

    /// <summary>Receives the call arguments; the returned object is serialized as the result text.</summary>
    public Func<JObject, Task<object?>> Handler { get; }

    public Tool(string name, string description, JObject inputSchema, Func<JObject, Task<object?>> handler,
        params string[] capabilities)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Handler = handler;
        Capabilities = capabilities;
    }
}
