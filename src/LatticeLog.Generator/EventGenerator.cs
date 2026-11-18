using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LatticeLog.Generator;

/// <summary>
/// Incremental generator for [LatticeEvent] methods (P03-T03/T04). Generated code checks IsEnabled first,
/// builds the record without string formatting, JSON or reflection, and passes value types without boxing.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EventGenerator : IIncrementalGenerator
{
    private const string AttributeName = "LatticeLog.LatticeEventAttribute";
    private const string LockFileName = "latticelog.events.lock.json";

    internal static readonly DiagnosticDescriptor DuplicateId = new("LL0001", "Duplicate event id", "Event id {0} version {1} is declared by both '{2}' and '{3}'", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor UnknownPlaceholder = new("LL0002", "Unknown template placeholder", "Template placeholder '{{{0}}}' of event '{1}' does not match any parameter", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor UnsupportedType = new("LL0003", "Unsupported parameter type", "Parameter '{0}' of type '{1}' is not supported by typed events", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor InvalidSignature = new("LL0004", "Invalid event method", "Event method '{0}' must be 'static partial void', in a partial type, with an ILatticeLogger as first parameter: {1}", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor InvalidName = new("LL0005", "Invalid event name or id", "Event '{0}': {1}", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor NameConflict = new("LL0006", "Event id reused with another name", "Event id {0} is used with names '{1}' and '{2}'; ids must keep a stable identity", "LatticeLog", DiagnosticSeverity.Error, true);
    internal static readonly DiagnosticDescriptor SchemaChanged = new("LL0007", "Incompatible schema change", "Event {0} v{1} differs from {2}: {3}. Bump Version for incompatible changes", "LatticeLog", DiagnosticSeverity.Error, true);

    private static readonly Regex NameRegex = new("^[a-z0-9][a-z0-9_.\\-]{0,199}$", RegexOptions.CultureInvariant);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, ct) => Model.From(ctx, ct));

        var lockFile = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(LockFileName, StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) => t.GetText(ct)?.ToString())
            .Collect();

        context.RegisterSourceOutput(methods.Collect().Combine(lockFile), static (spc, input) => Emit(spc, input.Left, input.Right));
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Model?> models, ImmutableArray<string?> lockTexts)
    {
        var valid = new List<Model>();
        foreach (var m in models)
        {
            if (m is null) continue;
            foreach (var d in m.Diagnostics) spc.ReportDiagnostic(d);
            if (m.Diagnostics.Count == 0) valid.Add(m);
        }
        var byKey = new Dictionary<string, Model>();
        var byId = new Dictionary<uint, Model>();
        foreach (var m in valid)
        {
            var key = m.Id + "/" + m.Version;
            if (byKey.TryGetValue(key, out var other))
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, m.Location, m.Id, m.Version, other.MethodDisplay, m.MethodDisplay));
            else byKey[key] = m;
            if (byId.TryGetValue(m.Id, out var sameId) && sameId.EventName != m.EventName)
                spc.ReportDiagnostic(Diagnostic.Create(NameConflict, m.Location, m.Id, sameId.EventName, m.EventName));
            else byId[m.Id] = m;
        }
        var lockEntries = ParseLock(lockTexts.FirstOrDefault(t => t is not null));
        foreach (var m in valid)
        {
            if (lockEntries.TryGetValue(m.Id + "/" + m.Version, out var locked))
            {
                var current = m.Signature();
                if (current != locked)
                    spc.ReportDiagnostic(Diagnostic.Create(SchemaChanged, m.Location, m.Id, m.Version, LockFileName, $"expected '{locked}', found '{current}'"));
            }
        }
        foreach (var group in valid.GroupBy(m => m.TypeKey))
        {
            spc.AddSource(Sanitize(group.Key) + ".LatticeEvents.g.cs", SourceText.From(Render(group.ToList()), Encoding.UTF8));
        }
        // Emits the current schema snapshot so teams can commit it as the lock file (P01-T06).
        if (valid.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> Current event schema snapshot. Copy the JSON below to latticelog.events.lock.json and add it as AdditionalFiles to freeze contracts.");
            sb.AppendLine("/*");
            sb.AppendLine("[");
            var ordered = valid.OrderBy(v => v.Id).ThenBy(v => v.Version).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                sb.Append("  {\"id\":").Append(ordered[i].Id).Append(",\"version\":").Append(ordered[i].Version)
                  .Append(",\"signature\":\"").Append(ordered[i].Signature().Replace("\"", "\\\"")).Append("\"}");
                sb.AppendLine(i + 1 < ordered.Count ? "," : "");
            }
            sb.AppendLine("]");
            sb.AppendLine("*/");
            spc.AddSource("LatticeEvents.SchemaSnapshot.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    /// <summary>Minimal parser for [{"id":1,"version":1,"signature":"..."}] (no JSON dependency in the generator).</summary>
    private static Dictionary<string, string> ParseLock(string? text)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Regex.Matches(text, "\\{\\s*\"id\"\\s*:\\s*(\\d+)\\s*,\\s*\"version\"\\s*:\\s*(\\d+)\\s*,\\s*\"signature\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}"))
            result[m.Groups[1].Value + "/" + m.Groups[2].Value] = m.Groups[3].Value.Replace("\\\"", "\"");
        return result;
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string? s) => s is null ? "null" : SymbolDisplay.FormatLiteral(s, true);

    private static string Render(List<Model> models)
    {
        var first = models[0];
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        if (first.Namespace.Length > 0) sb.Append("namespace ").Append(first.Namespace).AppendLine(";");
        sb.AppendLine();
        var indent = 0;
        foreach (var t in first.TypeChain)
        {
            sb.Append(' ', indent * 4).Append(t).AppendLine();
            sb.Append(' ', indent * 4).AppendLine("{");
            indent++;
        }
        var pad = new string(' ', indent * 4);
        foreach (var m in models)
        {
            var desc = "__LatticeEvent_" + m.MethodName;
            sb.Append(pad).Append("private static readonly global::LatticeLog.EventDescriptor ").Append(desc).Append(" = new global::LatticeLog.EventDescriptor(")
              .Append(m.Id).Append("u, ").Append(Literal(m.EventName)).Append(", ").Append(m.Version).Append(", (global::LatticeLog.Severity)").Append(m.Level)
              .Append(", ").Append(Literal(m.Message)).Append(", new global::LatticeLog.EventField[] { ");
            sb.Append(string.Join(", ", m.Fields.Select(f => $"new global::LatticeLog.EventField({Literal(f.Name)}, global::LatticeLog.ValueKind.{f.Kind}, (global::LatticeLog.Sensitivity){f.Sensitivity}, null)")));
            sb.Append(" }, ").Append(Literal(m.Description)).AppendLine(");");
            sb.AppendLine();
            sb.Append(pad).Append("/// <summary>Event descriptor of <see cref=\"").Append(m.MethodName).AppendLine("\"/>.</summary>");
            sb.Append(pad).Append(m.Accessibility).Append(" static global::LatticeLog.EventDescriptor ").Append(m.MethodName).Append("Descriptor => ").Append(desc).AppendLine(";");
            sb.AppendLine();
            sb.Append(pad).Append(m.Accessibility).Append(" static partial void ").Append(m.MethodName).Append('(').Append(m.ParameterList).AppendLine(")");
            sb.Append(pad).AppendLine("{");
            sb.Append(pad).Append("    if (!").Append(m.LoggerParam).Append(".IsEnabled((global::LatticeLog.Severity)").Append(m.Level).AppendLine(")) return;");
            sb.Append(pad).Append("    ").Append(m.LoggerParam).AppendLine(".Write(new global::LatticeLog.LogRecord");
            sb.Append(pad).AppendLine("    {");
            sb.Append(pad).Append("        Severity = (global::LatticeLog.Severity)").Append(m.Level).AppendLine(",");
            sb.Append(pad).Append("        Event = ").Append(desc).AppendLine(",");
            if (m.Fields.Count == 0) sb.Append(pad).AppendLine("        Attributes = global::LatticeLog.LogRecord.NoAttributes,");
            else
            {
                sb.Append(pad).AppendLine("        Attributes = new global::LatticeLog.LogAttribute[]");
                sb.Append(pad).AppendLine("        {");
                foreach (var f in m.Fields)
                    sb.Append(pad).Append("            new global::LatticeLog.LogAttribute(").Append(Literal(f.Name)).Append(", ").Append(f.Conversion).AppendLine("),");
                sb.Append(pad).AppendLine("        },");
            }
            if (m.ExceptionParam is not null)
                sb.Append(pad).Append("        Exception = ").Append(m.ExceptionParam).Append(" is null ? null : global::LatticeLog.ExceptionInfo.FromException(").Append(m.ExceptionParam).AppendLine("),");
            sb.Append(pad).AppendLine("    });");
            sb.Append(pad).AppendLine("}");
            sb.AppendLine();
        }
        for (var i = first.TypeChain.Count - 1; i >= 0; i--)
        {
            indent--;
            sb.Append(' ', indent * 4).AppendLine("}");
        }
        return sb.ToString();
    }

    private sealed class Field
    {
        public string Name = "";
        public string Kind = "";
        public string Conversion = "";
        public int Sensitivity;
    }

    private sealed class Model
    {
        public uint Id;
        public string EventName = "";
        public int Level = 9;
        public int Version = 1;
        public string? Message;
        public string? Description;
        public string MethodName = "";
        public string MethodDisplay = "";
        public string Accessibility = "public";
        public string Namespace = "";
        public List<string> TypeChain = new();
        public string TypeKey = "";
        public string LoggerParam = "logger";
        public string? ExceptionParam;
        public string ParameterList = "";
        public List<Field> Fields = new();
        public List<Diagnostic> Diagnostics = new();
        public Location? Location;

        public string Signature() => EventName + "|" + Level + "|" + string.Join(",", Fields.Select(f => f.Name + ":" + f.Kind + (f.Sensitivity != 0 ? "!" + f.Sensitivity : "")));

        public static Model? From(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
        {
            if (ctx.TargetSymbol is not IMethodSymbol method) return null;
            var syntax = (MethodDeclarationSyntax)ctx.TargetNode;
            var m = new Model
            {
                MethodName = method.Name,
                MethodDisplay = method.ContainingType.Name + "." + method.Name,
                Location = syntax.Identifier.GetLocation(),
                Accessibility = method.DeclaredAccessibility switch
                {
                    Microsoft.CodeAnalysis.Accessibility.Public => "public",
                    Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
                    Microsoft.CodeAnalysis.Accessibility.Private => "private",
                    Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
                    Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
                    Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
                    _ => "internal",
                },
            };
            var attr = ctx.Attributes[0];
            foreach (var kv in attr.NamedArguments)
            {
                switch (kv.Key)
                {
                    case "Id": m.Id = Convert.ToUInt32(kv.Value.Value); break;
                    case "Name": m.EventName = kv.Value.Value as string ?? ""; break;
                    case "Level": m.Level = Convert.ToInt32(kv.Value.Value); break;
                    case "Version": m.Version = Convert.ToInt32(kv.Value.Value); break;
                    case "Message": m.Message = kv.Value.Value as string; break;
                    case "Description": m.Description = kv.Value.Value as string; break;
                }
            }

            var problems = new List<string>();
            if (!method.IsStatic) problems.Add("not static");
            if (!method.ReturnsVoid) problems.Add("must return void");
            if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword)) problems.Add("not partial");
            if (method.IsGenericMethod) problems.Add("generic methods are not supported");
            for (var t = method.ContainingType; t is not null; t = t.ContainingType)
            {
                var partial = t.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is TypeDeclarationSyntax tds && tds.Modifiers.Any(SyntaxKind.PartialKeyword));
                if (!partial) problems.Add($"containing type '{t.Name}' is not partial");
                if (t.IsGenericType) problems.Add("generic containing types are not supported");
            }
            if (method.Parameters.Length == 0 || !IsLogger(method.Parameters[0].Type)) problems.Add("first parameter must be LatticeLog.ILatticeLogger");
            if (problems.Count > 0)
            {
                m.Diagnostics.Add(Diagnostic.Create(InvalidSignature, m.Location, m.MethodDisplay, string.Join("; ", problems)));
                return m;
            }
            if (m.Id == 0) m.Diagnostics.Add(Diagnostic.Create(InvalidName, m.Location, m.MethodDisplay, "Id must be > 0"));
            if (!NameRegex.IsMatch(m.EventName)) m.Diagnostics.Add(Diagnostic.Create(InvalidName, m.Location, m.MethodDisplay, $"Name '{m.EventName}' must match {NameRegex}"));
            if (m.Level is < 1 or > 24) m.Diagnostics.Add(Diagnostic.Create(InvalidName, m.Location, m.MethodDisplay, "Level must be an OpenTelemetry severity number 1..24"));
            if (m.Version is < 1 or > 65535) m.Diagnostics.Add(Diagnostic.Create(InvalidName, m.Location, m.MethodDisplay, "Version must be 1..65535"));

            m.LoggerParam = Escape(method.Parameters[0].Name);
            var paramTexts = new List<string>();
            foreach (var p in method.Parameters)
            {
                paramTexts.Add(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes)) + " " + Escape(p.Name));
            }
            m.ParameterList = string.Join(", ", paramTexts);
            foreach (var p in method.Parameters.Skip(1))
            {
                if (IsException(p.Type))
                {
                    if (m.ExceptionParam is not null) m.Diagnostics.Add(Diagnostic.Create(UnsupportedType, p.Locations.FirstOrDefault(), p.Name, "second Exception parameter"));
                    m.ExceptionParam = Escape(p.Name);
                    continue;
                }
                var (kind, conv) = Map(p.Type, Escape(p.Name));
                if (kind is null)
                {
                    m.Diagnostics.Add(Diagnostic.Create(UnsupportedType, p.Locations.FirstOrDefault() ?? m.Location, p.Name, p.Type.ToDisplayString()));
                    continue;
                }
                var sens = 0;
                foreach (var a in p.GetAttributes())
                {
                    if (a.AttributeClass?.ToDisplayString() == "LatticeLog.SensitiveAttribute")
                        sens = a.ConstructorArguments.Length > 0 ? Convert.ToInt32(a.ConstructorArguments[0].Value) : 2;
                }
                m.Fields.Add(new Field { Name = p.Name, Kind = kind, Conversion = conv!, Sensitivity = sens });
            }
            if (m.Message is not null)
            {
                foreach (Match ph in Regex.Matches(m.Message.Replace("{{", "").Replace("}}", ""), "\\{([^{}:]+)(:[^{}]*)?\\}"))
                {
                    var name = ph.Groups[1].Value;
                    if (!m.Fields.Any(f => f.Name == name) && name != m.ExceptionParam)
                        m.Diagnostics.Add(Diagnostic.Create(UnknownPlaceholder, m.Location, name, m.EventName));
                }
            }
            var type = method.ContainingType;
            m.Namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            var chain = new List<string>();
            for (var t = type; t is not null; t = t.ContainingType)
            {
                var kw = t.IsRecord ? (t.TypeKind == TypeKind.Struct ? "record struct" : "record") : t.TypeKind == TypeKind.Struct ? "struct" : t.TypeKind == TypeKind.Interface ? "interface" : "class";
                var mods = t.IsStatic ? "static partial " : "partial ";
                chain.Insert(0, mods + kw + " " + t.Name);
            }
            m.TypeChain = chain;
            m.TypeKey = type.ToDisplayString();
            return m;
        }

        private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private static bool IsLogger(ITypeSymbol t)
        {
            if (t.ToDisplayString() == "LatticeLog.ILatticeLogger") return true;
            return t.AllInterfaces.Any(i => i.ToDisplayString() == "LatticeLog.ILatticeLogger");
        }

        private static bool IsException(ITypeSymbol t)
        {
            for (var b = t; b is not null; b = b.BaseType)
                if (b.ToDisplayString() is "System.Exception" or "System.Exception?") return true;
            return false;
        }

        private static (string? Kind, string? Conversion) Map(ITypeSymbol type, string name)
        {
            var nullable = false;
            var t = type;
            if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nt)
            {
                nullable = true;
                t = nt.TypeArguments[0];
            }
            string? kind; string expr;
            var v = nullable ? name + ".Value" : name;
            switch (t.SpecialType)
            {
                case SpecialType.System_String: return ("String", $"global::LatticeLog.Value.From({name})");
                case SpecialType.System_Boolean: kind = "Bool"; expr = $"global::LatticeLog.Value.From({v})"; break;
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64: kind = "Int64"; expr = $"global::LatticeLog.Value.From((long){v})"; break;
                case SpecialType.System_UInt64: kind = "UInt64"; expr = $"global::LatticeLog.Value.From({v})"; break;
                case SpecialType.System_Single:
                case SpecialType.System_Double: kind = "Double"; expr = $"global::LatticeLog.Value.From((double){v})"; break;
                case SpecialType.System_Decimal: kind = "Decimal"; expr = $"global::LatticeLog.Value.From({v})"; break;
                case SpecialType.System_DateTime: kind = "Timestamp"; expr = $"global::LatticeLog.Value.FromTimestamp(global::LatticeLog.UnixTime.ToUnixNanos({v}))"; break;
                default:
                    {
                        var full = t.ToDisplayString();
                        if (full == "System.DateTimeOffset") { kind = "Timestamp"; expr = $"global::LatticeLog.Value.From({v})"; }
                        else if (full == "System.TimeSpan") { kind = "Duration"; expr = $"global::LatticeLog.Value.From({v})"; }
                        else if (full == "System.Guid") { kind = "Uuid"; expr = $"global::LatticeLog.Value.From({v})"; }
                        else if (t is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) return ("Bytes", $"global::LatticeLog.Value.From({name})");
                        else if (t.TypeKind == TypeKind.Enum) { kind = "String"; expr = $"global::LatticeLog.Value.From({v}.ToString())"; }
                        else if (full == "LatticeLog.Value") { kind = "Null"; expr = v; }
                        else return (null, null);
                        break;
                    }
            }
            if (nullable) expr = $"({name}.HasValue ? {expr} : global::LatticeLog.Value.Null)";
            return (kind, expr);
        }
    }
}
