using LatticeLog.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace LatticeLog.Extensions.Logging;

/// <summary>
/// Bridge from Microsoft.Extensions.Logging to LatticeLog (P03-T11). Structured state (message templates) becomes
/// ordered attributes; EventId becomes event_id/event_name; scopes become immutable LatticeLog scope contexts.
/// Cost note: MEL formats the message and allocates its state object; use generated typed events on hot paths.
/// </summary>
[ProviderAlias("LatticeLog")]
public sealed class LatticeLogProvider(ILatticeLogger target) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;

    public ILogger CreateLogger(string categoryName) => new Bridge(target, categoryName, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    public static Severity Map(MelLogLevel level) => level switch
    {
        MelLogLevel.Trace => Severity.Trace,
        MelLogLevel.Debug => Severity.Debug,
        MelLogLevel.Information => Severity.Info,
        MelLogLevel.Warning => Severity.Warn,
        MelLogLevel.Error => Severity.Error,
        MelLogLevel.Critical => Severity.Fatal,
        _ => Severity.Unspecified,
    };

    private sealed class Bridge(ILatticeLogger target, string category, Func<IExternalScopeProvider?> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => scopes() is { } sp ? sp.Push(state) : target.BeginScope(ContextKind.Scope, null, ToAttributes(state, out _));

        public bool IsEnabled(MelLogLevel logLevel) => logLevel != MelLogLevel.None && target.IsEnabled(Map(logLevel));

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var severity = Map(logLevel);
            if (logLevel == MelLogLevel.None || !target.IsEnabled(severity)) return;
            var attrs = new List<LogAttribute>(ToAttributes(state, out var template)) { new("logger.category", category) };
            if (template is not null) attrs.Add(new LogAttribute("message.template", template));
            // External scopes (when the host owns scope handling) are flattened into attributes, outermost first.
            scopes()?.ForEachScope(static (scope, list) =>
            {
                foreach (var a in ToAttributes(scope, out _)) list.Add(a with { Key = "scope." + a.Key });
            }, attrs);
            var record = new LogRecord
            {
                Severity = severity,
                EventId = eventId.Id > 0 ? (uint)eventId.Id : 0,
                EventName = eventId.Name,
                Attributes = attrs.ToArray(),
                Exception = exception is null ? null : ExceptionInfo.FromException(exception),
            };
            record.SetBody(Value.From(formatter(state, exception)));
            target.Write(record);
        }

        private static LogAttribute[] ToAttributes(object? state, out string? template)
        {
            template = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
            {
                var list = new List<LogAttribute>();
                foreach (var kv in kvs)
                {
                    if (kv.Key == "{OriginalFormat}") { template = kv.Value as string; continue; }
                    list.Add(new LogAttribute(kv.Key, ValueConverter.FromObject(kv.Value)));
                }
                return list.ToArray();
            }
            return state is null ? [] : [new LogAttribute("scope", ValueConverter.FromObject(state))];
        }
    }
}

public static class LatticeLogLoggingBuilderExtensions
{
    /// <summary>Adds LatticeLog as a Microsoft.Extensions.Logging provider writing to <paramref name="logger"/>.</summary>
    public static ILoggingBuilder AddLatticeLog(this ILoggingBuilder builder, ILatticeLogger logger)
    {
        builder.Services.AddSingleton<ILoggerProvider>(new LatticeLogProvider(logger));
        return builder;
    }
}
