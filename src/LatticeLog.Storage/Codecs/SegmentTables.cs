using LatticeLog.Storage.Format;

namespace LatticeLog.Storage.Codecs;

/// <summary>
/// Per-segment catalogs of event descriptors and context snapshots. References are 1-based
/// (0 = none) and local to the segment, so every segment is self-contained (P04-T05).
/// </summary>
public sealed class SegmentTables
{
    private readonly Dictionary<EventDescriptor, int> _schemaIndex = new();
    private readonly Dictionary<ContextSnapshot, int> _contextIndex = new(ReferenceEqualityComparer.Instance);

    public List<EventDescriptor> Schemas { get; } = [];
    public List<ContextSnapshot> Contexts { get; } = [];

    public int SchemaRef(EventDescriptor d) => _schemaIndex.TryGetValue(d, out var i) ? i : throw new InvalidOperationException("Schema not registered in segment.");
    public int ContextRef(ContextSnapshot c) => _contextIndex.TryGetValue(c, out var i) ? i : throw new InvalidOperationException("Context not registered in segment.");

    public EventDescriptor GetSchema(ulong reference)
    {
        if (reference == 0 || reference > (ulong)Schemas.Count) throw new LlogFormatException($"Unknown schema reference {reference}.");
        return Schemas[(int)reference - 1];
    }

    public ContextSnapshot GetContext(ulong reference)
    {
        if (reference == 0 || reference > (ulong)Contexts.Count) throw new LlogFormatException($"Unknown context reference {reference}.");
        return Contexts[(int)reference - 1];
    }

    /// <summary>Collects descriptors and contexts used by <paramref name="records"/> that are not yet defined.</summary>
    public (List<EventDescriptor> NewSchemas, List<ContextSnapshot> NewContexts) CollectNew(IReadOnlyList<LogRecord> records, int start, int count)
    {
        var ns = new List<EventDescriptor>();
        var nc = new List<ContextSnapshot>();
        var pendingS = new HashSet<EventDescriptor>();
        var pendingC = new HashSet<ContextSnapshot>(ReferenceEqualityComparer.Instance);
        for (var i = start; i < start + count; i++)
        {
            var r = records[i];
            if (r.Event is { } e && !_schemaIndex.ContainsKey(e) && pendingS.Add(e)) ns.Add(e);
            if (r.Context is { } c) AddContextChain(c, nc, pendingC);
        }
        return (ns, nc);
    }

    private void AddContextChain(ContextSnapshot c, List<ContextSnapshot> output, HashSet<ContextSnapshot> pending)
    {
        if (_contextIndex.ContainsKey(c) || pending.Contains(c)) return;
        if (c.Parent is not null) AddContextChain(c.Parent, output, pending);
        pending.Add(c);
        output.Add(c);
    }

    public void RegisterSchema(EventDescriptor d)
    {
        if (_schemaIndex.ContainsKey(d)) return;
        Schemas.Add(d);
        _schemaIndex[d] = Schemas.Count;
    }

    public void RegisterContext(ContextSnapshot c)
    {
        if (_contextIndex.ContainsKey(c)) return;
        if (c.Parent is not null && !_contextIndex.ContainsKey(c.Parent)) throw new InvalidOperationException("Parent context must be registered first.");
        Contexts.Add(c);
        _contextIndex[c] = Contexts.Count;
    }

    // ---- Schema block -------------------------------------------------------------------

    public void EncodeSchemaBlock(IReadOnlyList<EventDescriptor> schemas, ByteWriter w)
    {
        w.WriteVarUInt((ulong)schemas.Count);
        foreach (var s in schemas)
        {
            w.WriteVarUInt(s.Id);
            w.WriteString(ValueCodec.SanitizeKey(s.Name));
            w.WriteVarUInt(s.Version);
            w.WriteByte((byte)s.Severity);
            byte p = 0;
            if (s.MessageTemplate is not null) p |= 1;
            if (s.Description is not null) p |= 2;
            w.WriteByte(p);
            if (s.MessageTemplate is not null) w.WriteString(ValueCodec.SanitizeKey(s.MessageTemplate));
            if (s.Description is not null) w.WriteString(ValueCodec.SanitizeKey(s.Description));
            w.WriteVarUInt((ulong)s.Fields.Count);
            foreach (var f in s.Fields)
            {
                w.WriteString(ValueCodec.SanitizeKey(f.Name));
                w.WriteByte((byte)f.Kind);
                w.WriteByte((byte)f.Sensitivity);
                w.WriteByte(f.Description is null ? (byte)0 : (byte)1);
                if (f.Description is not null) w.WriteString(ValueCodec.SanitizeKey(f.Description));
            }
            RegisterSchema(s);
        }
    }

    public void DecodeSchemaBlock(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var n = r.ReadCount(5);
        for (var i = 0; i < n; i++)
        {
            var id = r.ReadVarUInt();
            if (id > uint.MaxValue) throw new LlogFormatException("Event id out of range.");
            var name = r.ReadString();
            var version = r.ReadVarUInt();
            if (version > ushort.MaxValue) throw new LlogFormatException("Schema version out of range.");
            var sev = r.ReadByte();
            if (sev > SeverityExtensions.MaxNumber) throw new LlogFormatException("Severity out of range.");
            var p = r.ReadByte();
            if ((p & ~3) != 0) throw new LlogFormatException("Invalid schema presence bits.");
            var tmpl = (p & 1) != 0 ? r.ReadString() : null;
            var desc = (p & 2) != 0 ? r.ReadString() : null;
            var fc = r.ReadCount(4, Llog.MaxAttributes);
            var fields = new EventField[fc];
            for (var j = 0; j < fc; j++)
            {
                var fname = r.ReadString();
                var kind = r.ReadByte();
                if (!Enum.IsDefined((ValueKind)kind)) throw new LlogFormatException($"Unknown field kind {kind}.");
                var sens = r.ReadByte();
                if (sens > 2) throw new LlogFormatException($"Unknown sensitivity {sens}.");
                var fp = r.ReadByte();
                if (fp > 1) throw new LlogFormatException("Invalid field presence bits.");
                fields[j] = new EventField(fname, (ValueKind)kind, (Sensitivity)sens, fp == 1 ? r.ReadString() : null);
            }
            var d = new EventDescriptor((uint)id, name, (ushort)version, (Severity)sev, tmpl, fields, desc);
            Schemas.Add(d);
            _schemaIndex.TryAdd(d, Schemas.Count);
        }
        if (!r.End) throw new LlogFormatException("Trailing bytes in schema block.");
    }

    // ---- Context block ------------------------------------------------------------------

    public void EncodeContextBlock(IReadOnlyList<ContextSnapshot> contexts, ByteWriter w)
    {
        w.WriteVarUInt((ulong)contexts.Count);
        foreach (var c in contexts)
        {
            w.WriteVarUInt(c.Parent is null ? 0UL : (ulong)ContextRef(c.Parent));
            w.WriteByte((byte)c.Kind);
            w.WriteByte(c.Name is null ? (byte)0 : (byte)1);
            if (c.Name is not null) w.WriteString(ValueCodec.SanitizeKey(c.Name));
            w.WriteVarUInt((ulong)c.Attributes.Count);
            foreach (var a in c.Attributes)
            {
                w.WriteString(ValueCodec.SanitizeKey(a.Key));
                ValueCodec.Write(w, a.Value);
            }
            RegisterContext(c);
        }
    }

    public void DecodeContextBlock(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var n = r.ReadCount(4);
        for (var i = 0; i < n; i++)
        {
            var parentRef = r.ReadVarUInt();
            ContextSnapshot? parent = null;
            if (parentRef != 0)
            {
                if (parentRef > (ulong)Contexts.Count) throw new LlogFormatException("Context parent must be defined before the child.");
                parent = Contexts[(int)parentRef - 1];
            }
            var kind = r.ReadByte();
            if (kind > (byte)ContextKind.Custom) throw new LlogFormatException($"Unknown context kind {kind}.");
            var hasName = r.ReadByte();
            if (hasName > 1) throw new LlogFormatException("Invalid context presence bits.");
            var name = hasName == 1 ? r.ReadString() : null;
            var ac = r.ReadCount(2, Llog.MaxAttributes);
            var attrs = new LogAttribute[ac];
            for (var j = 0; j < ac; j++)
            {
                var k = r.ReadString();
                attrs[j] = new LogAttribute(k, ValueCodec.Read(ref r));
            }
            if (parent is not null && parent.Depth + 1 > ContextSnapshot.MaxDepth) throw new LlogFormatException("Context nesting too deep.");
            var c = new ContextSnapshot((ContextKind)kind, name, attrs, parent);
            Contexts.Add(c);
            _contextIndex[c] = Contexts.Count;
        }
        if (!r.End) throw new LlogFormatException("Trailing bytes in context block.");
    }
}
