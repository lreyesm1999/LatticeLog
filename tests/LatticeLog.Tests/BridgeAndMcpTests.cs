using System.Text.Json.Nodes;
using LatticeLog.Core;
using LatticeLog.Extensions.Logging;
using LatticeLog.Mcp;
using Microsoft.Extensions.Logging;

namespace LatticeLog.Tests;

public class BridgeAndMcpTests
{
    private sealed class Sink : IRecordSink
    {
        public readonly List<LogRecord> Records = [];
        public ulong LastSequence { get; private set; }
        public long WriteBatch(IReadOnlyList<LogRecord> b) { lock (Records) foreach (var r in b) { r.Sequence = ++LastSequence; Records.Add(r); } return 0; }
        public AckLevel Flush(AckLevel l) => l;
        public void Dispose() { }
    }

    [Fact]
    public void Microsoft_extensions_logging_bridge_maps_structured_state()
    {
        var sink = new Sink();
        var ll = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false, MinimumLevel = Severity.Debug });
        using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddLatticeLog(ll)))
        {
            var log = factory.CreateLogger("Orders.Api");
            using (log.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "r-1" }))
                log.LogWarning(new EventId(42, "OrderSlow"), "Order {OrderId} took {Elapsed} ms", 991, 1234.5);
            log.LogError(new InvalidOperationException("boom"), "Failed");
            log.LogTrace("filtered by LatticeLog minimum level");
        }
        ll.Dispose();
        Assert.Equal(2, sink.Records.Count);
        var r = sink.Records[0];
        Assert.Equal(Severity.Warn, r.Severity);
        Assert.Equal(42u, r.EventId);
        Assert.Equal("OrderSlow", r.EventName);
        Assert.Equal("Order 991 took 1234.5 ms", r.Body.AsString());
        Assert.Equal(991L, r.Attributes.Single(a => a.Key == "OrderId").Value.AsInt64());
        Assert.Equal(1234.5, r.Attributes.Single(a => a.Key == "Elapsed").Value.AsDouble());
        Assert.Equal("Order {OrderId} took {Elapsed} ms", r.Attributes.Single(a => a.Key == "message.template").Value.AsString());
        Assert.Equal("r-1", r.Attributes.Single(a => a.Key == "scope.RequestId").Value.AsString());
        Assert.Equal("System.InvalidOperationException", sink.Records[1].Exception!.Type);
    }

    [Fact]
    public void Mcp_server_is_read_only_and_bounded()
    {
        var (root, incident) = EvidenceAndInteropTests.BuildIncidentStore();
        var server = new McpServer(root, new McpLimits { MaxCallsPerSession = 5 });
        var init = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""")!)!;
        Assert.Equal(McpServer.SupportedProtocolVersions[0], (string?)init["result"]!["protocolVersion"]);
        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var tools = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!)!["result"]!["tools"]!.AsArray();
        Assert.All(tools, t => Assert.True((bool)t!["annotations"]!["readOnlyHint"]!));
        Assert.DoesNotContain(tools, t => t!["inputSchema"]!["properties"]!.AsObject().Any(p => p.Key.Contains("path")));
        var trace = JsonNode.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_trace\",\"arguments\":{\"trace_id\":\"" + incident + "\"}}}")!)!["result"]!;
        Assert.False((bool)trace["isError"]!);
        Assert.Equal(4, (int)trace["structuredContent"]!["coverage"]!["returned_events"]!);
        var bad = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_events","arguments":{"where":"level >="}}}""")!)!["result"]!;
        Assert.True((bool)bad["isError"]!);
        var unknown = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":5,"method":"resources/write"}""")!)!;
        Assert.Equal(-32601, (int)unknown["error"]!["code"]!);
        for (var i = 0; i < 4; i++) server.Handle("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_event_schema","arguments":{}}}""");
        var limited = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_event_schema","arguments":{}}}""")!)!["result"]!;
        Assert.True((bool)limited["isError"]!);
        Assert.Contains("limit", limited["content"]![0]!["text"]!.ToString());
    }
}
