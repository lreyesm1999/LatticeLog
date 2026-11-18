using System.Collections.Immutable;
using LatticeLog.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LatticeLog.Tests;

public static partial class InventoryEvents
{
    [LatticeEvent(Id = 2101, Name = "inventory.stock.reservation.failed", Level = Severity.Error,
        Message = "Could not reserve {requested} units of {productId}; {available} available")]
    public static partial void StockReservationFailed(ILatticeLogger logger, long productId, int requested, int available, Exception? cause);

    [LatticeEvent(Id = 2102, Name = "inventory.stock.reserved", Level = Severity.Info)]
    public static partial void StockReserved(ILatticeLogger logger, long productId, int quantity, Guid reservationId, DateTimeOffset expiresAt, TimeSpan holdFor, decimal price, int? warehouse);

    [LatticeEvent(Id = 2103, Name = "auth.login", Level = Severity.Info)]
    internal static partial void Login(ILatticeLogger logger, string user, [Sensitive] string password, DayOfWeek day);

    [LatticeEvent(Id = 2104, Name = "debug.noise", Level = Severity.Debug)]
    public static partial void Noise(ILatticeLogger logger, int n);
}

public class GeneratorTests
{
    private sealed class CaptureSink : IRecordSink
    {
        public readonly List<LogRecord> Records = [];
        public ulong LastSequence { get; private set; }
        public long WriteBatch(IReadOnlyList<LogRecord> b) { lock (Records) { foreach (var r in b) { r.Sequence = ++LastSequence; Records.Add(r); } } return 0; }
        public AckLevel Flush(AckLevel l) => l;
        public void Dispose() { }
    }

    [Fact]
    public void Generated_events_produce_typed_records()
    {
        var sink = new CaptureSink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false });
        var ex = new InvalidOperationException("warehouse timeout", new TimeoutException("inner"));
        InventoryEvents.StockReservationFailed(logger, 42, 5, 1, ex);
        var rid = Guid.NewGuid();
        InventoryEvents.StockReserved(logger, 42, 3, rid, DateTimeOffset.UnixEpoch.AddDays(1), TimeSpan.FromSeconds(30), 9.99m, null);
        InventoryEvents.Login(logger, "alice", "hunter2", DayOfWeek.Monday);
        InventoryEvents.Noise(logger, 1); // below Info: generated code returns before allocating
        logger.Dispose();
        Assert.Equal(3, sink.Records.Count);
        Assert.Equal(0, logger.GetStats().FilteredByLevel); // never reached Write
        var r0 = sink.Records[0];
        Assert.Equal(2101u, r0.Event!.Id);
        Assert.Equal(Severity.Error, r0.Severity);
        Assert.Equal(["productId", "requested", "available"], r0.Attributes.Select(a => a.Key));
        Assert.Equal(ValueKind.Int64, r0.Attributes[1].Value.Kind);
        Assert.Equal("System.InvalidOperationException", r0.Exception!.Type);
        Assert.Single(r0.Exception.Inner);
        Assert.Equal("Could not reserve 5 units of 42; 1 available", LatticeLog.Storage.Json.CanonicalJson.RenderTemplate(r0.Event.MessageTemplate!, r0.Attributes));
        var r1 = sink.Records[1];
        Assert.Equal(rid, r1.Attributes[2].Value.AsGuid());
        Assert.Equal(ValueKind.Timestamp, r1.Attributes[3].Value.Kind);
        Assert.Equal(30_000_000_000L, r1.Attributes[4].Value.AsDurationNanos());
        Assert.Equal(9.99m, r1.Attributes[5].Value.AsDecimal());
        Assert.True(r1.Attributes[6].Value.IsNull);
        var r2 = sink.Records[2];
        Assert.Equal(Sensitivity.Secret, r2.Event!.Fields[1].Sensitivity);
        Assert.Equal("[REDACTED]", r2.Attributes[1].Value.AsString());
        Assert.Equal("Monday", r2.Attributes[2].Value.AsString());
        Assert.Equal(InventoryEvents.StockReservationFailedDescriptor, r0.Event);
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string source, string? lockFile = null)
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(ILatticeLogger).Assembly.Location));
        var compilation = CSharpCompilation.Create("t", [CSharpSyntaxTree.ParseText(source)], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var asm = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "LatticeLog.Generator.dll"));
        var gen = ((IIncrementalGenerator)Activator.CreateInstance(asm.GetType("LatticeLog.Generator.EventGenerator")!)!).AsSourceGenerator();
        var texts = lockFile is null ? ImmutableArray<AdditionalText>.Empty : ImmutableArray.Create<AdditionalText>(new InMemoryText("latticelog.events.lock.json", lockFile));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([gen], texts);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diags);
        return diags.AddRange(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }

    [Theory]
    [InlineData("[LatticeEvent(Id=1, Name=\"a.b\")] public static partial void A(ILatticeLogger l, int x); [LatticeEvent(Id=1, Name=\"a.b\")] public static partial void B(ILatticeLogger l, int y);", "LL0001")]
    [InlineData("[LatticeEvent(Id=1, Name=\"a.b\", Message=\"{nope}\")] public static partial void A(ILatticeLogger l, int x);", "LL0002")]
    [InlineData("[LatticeEvent(Id=1, Name=\"a.b\")] public static partial void A(ILatticeLogger l, System.IO.Stream s);", "LL0003")]
    [InlineData("[LatticeEvent(Id=1, Name=\"a.b\")] public static partial void A(int x);", "LL0004")]
    [InlineData("[LatticeEvent(Id=1, Name=\"Bad Name\")] public static partial void A(ILatticeLogger l);", "LL0005")]
    [InlineData("[LatticeEvent(Id=1, Name=\"a.b\")] public static partial void A(ILatticeLogger l); [LatticeEvent(Id=1, Version=2, Name=\"c.d\")] public static partial void B(ILatticeLogger l);", "LL0006")]
    public void Generator_reports_compile_time_errors(string members, string expectedId)
    {
        var src = "using LatticeLog; namespace N; public static partial class E { " + members + " }";
        var diags = RunGenerator(src);
        Assert.Contains(diags, d => d.Id == expectedId);
    }

    [Fact]
    public void Generator_detects_incompatible_change_against_lock_file()
    {
        var src = "using LatticeLog; namespace N; public static partial class E { [LatticeEvent(Id=7, Name=\"order.paid\")] public static partial void Paid(ILatticeLogger l, long orderId, string currency); }";
        var ok = "[{\"id\":7,\"version\":1,\"signature\":\"order.paid|9|orderId:Int64,currency:String\"}]";
        Assert.DoesNotContain(RunGenerator(src, ok), d => d.Id == "LL0007");
        var changed = "[{\"id\":7,\"version\":1,\"signature\":\"order.paid|9|orderId:String,currency:String\"}]";
        Assert.Contains(RunGenerator(src, changed), d => d.Id == "LL0007");
    }

    [Fact]
    public void Valid_source_compiles_without_errors()
    {
        var src = "using LatticeLog; namespace N; public static partial class E { [LatticeEvent(Id=9, Name=\"ok.event\", Message=\"x={x}\")] public static partial void Ok(ILatticeLogger l, int x, System.Exception? e); }";
        Assert.Empty(RunGenerator(src).Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
