using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace TFWRMCP;

/// <summary>Streamable HTTP transport (POST, JSON responses only) bound to 127.0.0.1.</summary>
public sealed class McpServer : IDisposable
{
    HttpListener listener = new();
    readonly McpDispatcher dispatcher;
    readonly Action<string> log;

    public McpServer(IGame game, IEnumerable<Tool> tools, int port, Action<string>? log = null)
    {
        this.log = log ?? (_ => { });
        dispatcher = new McpDispatcher(game, tools, this.log);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public bool IsListening => listener.IsListening;

    /// <summary>Throws when the port cannot be bound.</summary>
    public void Start()
    {
        listener.Start();
        var accepting = listener;
        _ = Task.Run(() => AcceptLoop(accepting));
    }

    /// <summary>
    /// Drops every open request, interrupts the Tool in progress (a Run keeps going) and
    /// listens on the port again. Throws when the port cannot be bound, leaving the server stopped.
    /// </summary>
    public void Restart(int port)
    {
        listener.Close();
        dispatcher.Interrupt();
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        Start();
    }

    async Task AcceptLoop(HttpListener accepting)
    {
        while (accepting.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await accepting.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Serve(context, accepting));
        }
    }

    async Task Serve(HttpListenerContext context, HttpListener accepting)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (!IsLocalOrigin(request.Headers["Origin"]))
            {
                await Respond(response, 403, null);
            }
            else if (request.HttpMethod != "POST")
            {
                response.AddHeader("Allow", "POST");
                await Respond(response, 405, null);
            }
            else
            {
                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var (status, json) = await dispatcher.Handle(body);
                await Respond(response, status, json);
            }
        }
        catch (Exception e)
        {
            // Requests of a listener a restart has closed are dropped on purpose.
            if (accepting == listener) log($"Request failed: {e}");
            try { response.Abort(); } catch { /* connection already gone */ }
        }
    }

    // A browser page cannot reach us unless it sends a foreign Origin; non-browser clients send none.
    static bool IsLocalOrigin(string? origin) =>
        origin == null || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Host == "127.0.0.1" || uri.Host == "localhost"));

    static async Task Respond(HttpListenerResponse response, int status, string? json)
    {
        response.StatusCode = status;
        if (json == null)
        {
            response.ContentLength64 = 0;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        response.Close();
    }

    public void Dispose() => listener.Close();
}
