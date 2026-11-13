using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class McpProtocolTests
{
    [Fact]
    public async Task Initialize_returns_server_info_and_tools_capability()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("initialize", new JObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "test", ["version"] = "1" },
        });

        var result = resp["result"]!;
        Assert.Equal("2025-06-18", (string)result["protocolVersion"]!);
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("tfwrmcp", (string)result["serverInfo"]!["name"]!);
        Assert.Equal(1, (int)resp["id"]!);
    }

    [Fact]
    public async Task Initialize_with_unknown_protocol_version_answers_with_a_supported_one()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("initialize", new JObject { ["protocolVersion"] = "1999-01-01" });

        Assert.Equal("2025-06-18", (string)resp["result"]!["protocolVersion"]!);
    }

    [Fact]
    public async Task Ping_returns_empty_result()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("ping");

        Assert.Empty(((JObject)resp["result"]!).Properties());
    }

    [Fact]
    public async Task Notification_is_accepted_without_a_body()
    {
        using var s = new TestServer(new FakeGame());
        using var resp = await s.PostRaw("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_method_is_a_method_not_found_error()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("resources/list");

        Assert.Equal(-32601, (int)resp["error"]!["code"]!);
    }

    [Fact]
    public async Task Malformed_json_is_a_parse_error()
    {
        using var s = new TestServer(new FakeGame());
        using var resp = await s.PostRaw("{not json");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(-32700, (int)body["error"]!["code"]!);
    }

    [Fact]
    public async Task Non_object_request_is_an_invalid_request()
    {
        using var s = new TestServer(new FakeGame());
        using var resp = await s.PostRaw("[1,2]");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(-32600, (int)body["error"]!["code"]!);
    }

    [Fact]
    public async Task Get_is_rejected_because_there_is_no_sse_stream()
    {
        using var s = new TestServer(new FakeGame());
        using var resp = await s.Http.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task Browser_origin_is_rejected()
    {
        using var s = new TestServer(new FakeGame());
        using var resp = await s.PostRaw("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Tools_list_describes_get_state_in_english_with_a_schema()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("tools/list");

        var tool = Assert.Single(resp["result"]!["tools"]!, t => (string)t["name"]! == "get_state");
        Assert.False(string.IsNullOrWhiteSpace((string)tool["description"]!));
        Assert.Equal("object", (string)tool["inputSchema"]!["type"]!);
    }

    [Fact]
    public async Task Calling_an_unknown_tool_is_an_invalid_params_error()
    {
        using var s = new TestServer(new FakeGame());
        var resp = await s.Rpc("tools/call", new JObject { ["name"] = "nope" });

        Assert.Equal(-32602, (int)resp["error"]!["code"]!);
    }

    [Fact]
    public async Task Port_already_in_use_makes_start_fail()
    {
        using var first = new TestServer(new FakeGame());
        using var second = new McpServer(new FakeGame(), new Tool[0], first.Url.Port);

        Assert.ThrowsAny<System.Exception>(() => second.Start());
    }
}
