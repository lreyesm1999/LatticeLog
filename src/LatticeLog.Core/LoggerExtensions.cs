using System.Globalization;

namespace LatticeLog.Core;

/// <summary>
/// Convenience API for free-text and ad-hoc structured records. These allocate a record and an attribute
/// array per call (documented cost, P03-T02); prefer generated typed events on hot paths.
/// </summary>
public static class LoggerExtensions
{
    public static bool Log(this ILatticeLogger logger, Severity severity, string? eventName, string? message, Exception? exception = null, params LogAttribute[] attributes)
    {
        if (!logger.IsEnabled(severity)) return false;
        var r = LogRecordPool.Rent(0); // pooled record; the caller's attribute array is referenced, never cleared or reused
        r.Severity = severity;
        r.EventName = eventName;
        r.Attributes = attributes.Length == 0 ? LogRecord.NoAttributes : attributes;
        r.Exception = exception is null ? null : ExceptionInfo.FromException(exception);
        if (message is not null) r.SetBody(Value.From(message));
        return logger.Write(r);
    }

    public static bool Trace(this ILatticeLogger l, string message, params LogAttribute[] a) => l.Log(Severity.Trace, null, message, null, a);
    public static bool Debug(this ILatticeLogger l, string message, params LogAttribute[] a) => l.Log(Severity.Debug, null, message, null, a);
    public static bool Info(this ILatticeLogger l, string message, params LogAttribute[] a) => l.Log(Severity.Info, null, message, null, a);
    public static bool Warn(this ILatticeLogger l, string message, params LogAttribute[] a) => l.Log(Severity.Warn, null, message, null, a);
    public static bool Error(this ILatticeLogger l, string message, params LogAttribute[] a) => l.Log(Severity.Error, null, message, null, a);
    public static bool Error(this ILatticeLogger l, Exception ex, string message, params LogAttribute[] a) => l.Log(Severity.Error, null, message, ex, a);
    public static bool Fatal(this ILatticeLogger l, Exception ex, string message, params LogAttribute[] a) => l.Log(Severity.Fatal, null, message, ex, a);

    /// <summary>Write a record for a descriptor with values in field order (used by generated code and dynamic producers).</summary>
    public static bool WriteEvent(this ILatticeLogger logger, EventDescriptor descriptor, Exception? exception, params Value[] values)
    {
        if (!logger.IsEnabled(descriptor.Severity)) return false;
        if (values.Length != descriptor.Fields.Count) throw new ArgumentException($"Event {descriptor.Name} expects {descriptor.Fields.Count} values, got {values.Length}.");
        var r = LogRecordPool.Rent(values.Length);
        var attrs = r.Attributes;
        for (var i = 0; i < values.Length; i++) attrs[i] = new LogAttribute(descriptor.Fields[i].Name, values[i]);
        r.Severity = descriptor.Severity;
        r.Event = descriptor;
        r.Exception = exception is null ? null : ExceptionInfo.FromException(exception);
        return logger.Write(r);
    }
}

/// <summary>Converts CLR objects to <see cref="Value"/> without reflection over arbitrary graphs (bounded depth).</summary>
public static class ValueConverter
{
    public static Value FromObject(object? o, int depth = 0)
    {
        if (depth > 8) return Value.From(o?.ToString());
        return o switch
        {
            null => Value.Null,
            Value v => v,
            string s => Value.From(s),
            bool b => Value.From(b),
            byte or sbyte or short or ushort or int or long => Value.From(Convert.ToInt64(o, CultureInfo.InvariantCulture)),
            uint u => Value.From((long)u),
            ulong ul => Value.From(ul),
            float f => Value.From((double)f),
            double d => Value.From(d),
            decimal m => Value.From(m),
            DateTime dt => Value.From(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)),
            DateTimeOffset dto => Value.From(dto),
            TimeSpan ts => Value.From(ts),
            Guid g => Value.From(g),
            byte[] bytes => Value.From(bytes),
            Enum e => Value.From(e.ToString()),
            IEnumerable<KeyValuePair<string, object?>> kvs => Value.From(kvs.Take(256).Select(kv => new LogAttribute(kv.Key, FromObject(kv.Value, depth + 1))).ToArray()),
            System.Collections.IEnumerable list when o is not string => Value.From(list.Cast<object?>().Take(256).Select(x => FromObject(x, depth + 1)).ToArray()),
            _ => Value.From(o.ToString()),
        };
    }
}
