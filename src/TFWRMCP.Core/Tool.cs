using System;
using System.Threading;
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

    /// <summary>
    /// Receives the call arguments and a token that is cancelled when the human takes over (F10); the returned object
    /// is serialized as the result text. A handler must stop promptly once cancelled and not start anything new.
    /// </summary>
    public Func<JObject, CancellationToken, Task<object?>> Handler { get; }

    public Tool(string name, string description, JObject inputSchema, Func<JObject, CancellationToken, Task<object?>> handler,
        params string[] capabilities)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Handler = handler;
        Capabilities = capabilities;
    }
}
