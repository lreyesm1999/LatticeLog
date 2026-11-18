using System.Globalization;
using System.Text;

namespace LatticeLog.Query;

/// <summary>Error in a query with the character position (P11-T02).</summary>
public sealed class QuerySyntaxException(string message, int position) : Exception($"{message} (at position {position})")
{
    public int Position { get; } = position;
}

public enum BuiltinField
{
    Attribute,
    Context,
    Time,
    Severity,
    Event,
    EventId,
    Service,
    TraceId,
    SpanId,
    OperationId,
    ParentOperationId,
    CorrelationId,
    Body,
    Sequence,
    ExceptionType,
    ExceptionMessage,
    Text,
}

public sealed record FieldRef(BuiltinField Kind, string? Name = null)
{
    public override string ToString() => Kind switch
    {
        BuiltinField.Attribute => Name!,
        BuiltinField.Context => "ctx." + Name,
        _ => QueryParser.BuiltinName(Kind),
    };
}

public enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }

/// <summary>Literal from the query text. Interpretation depends on the field (typed comparison, P11-T04).</summary>
public sealed record Literal(object? Raw, string Text)
{
    public override string ToString() => Raw switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''") + "'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Text,
    };
}

public abstract record Expr;
public sealed record AndExpr(Expr Left, Expr Right) : Expr { public override string ToString() => $"({Left} AND {Right})"; }
public sealed record OrExpr(Expr Left, Expr Right) : Expr { public override string ToString() => $"({Left} OR {Right})"; }
public sealed record NotExpr(Expr Inner) : Expr { public override string ToString() => $"NOT {Inner}"; }
public sealed record CompareExpr(FieldRef Field, CompareOp Op, Literal Value) : Expr
{
    public override string ToString() => $"{Field} {Op switch { CompareOp.Eq => "=", CompareOp.Ne => "!=", CompareOp.Lt => "<", CompareOp.Le => "<=", CompareOp.Gt => ">", _ => ">=" }} {Value}";
}
public sealed record InExpr(FieldRef Field, IReadOnlyList<Literal> Values) : Expr { public override string ToString() => $"{Field} IN ({string.Join(", ", Values)})"; }
public sealed record ExistsExpr(FieldRef Field) : Expr { public override string ToString() => $"EXISTS({Field})"; }
public sealed record ContainsExpr(FieldRef Field, string Needle) : Expr { public override string ToString() => $"{Field} CONTAINS '{Needle.Replace("'", "''")}'"; }
public sealed record StartsWithExpr(FieldRef Field, string Prefix) : Expr { public override string ToString() => $"{Field} STARTSWITH '{Prefix.Replace("'", "''")}'"; }
public sealed record TrueExpr : Expr { public static readonly TrueExpr Instance = new(); public override string ToString() => "TRUE"; }

/// <summary>
/// Recursive descent parser of the LatticeLog query language v1 (specs/query-language.md). The grammar has no
/// functions or code execution; inputs are bounded in length and nesting depth.
/// </summary>
public sealed class QueryParser
{
    public const int MaxQueryLength = 8192;
    public const int MaxDepth = 64;
    public const int MaxInValues = 1024;

    private enum T { Ident, String, Number, Op, LParen, RParen, Comma, End }
    private readonly record struct Tok(T Type, string Text, int Pos, object? Value = null);

    private readonly List<Tok> _toks;
    private int _i;
    private int _depth;

    private QueryParser(List<Tok> toks) => _toks = toks;

    public static Expr Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TrueExpr.Instance;
        if (text.Length > MaxQueryLength) throw new QuerySyntaxException($"Query longer than {MaxQueryLength} characters", MaxQueryLength);
        var p = new QueryParser(Lex(text));
        var e = p.ParseOr();
        if (p.Peek.Type != T.End) throw new QuerySyntaxException($"Unexpected '{p.Peek.Text}'", p.Peek.Pos);
        return e;
    }

    private static List<Tok> Lex(string s)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (c == '(') { list.Add(new Tok(T.LParen, "(", i++)); continue; }
            if (c == ')') { list.Add(new Tok(T.RParen, ")", i++)); continue; }
            if (c == ',') { list.Add(new Tok(T.Comma, ",", i++)); continue; }
            if (c is '\'' or '"')
            {
                var q = c;
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new QuerySyntaxException("Unterminated string", start);
                    if (s[i] == q)
                    {
                        if (i + 1 < s.Length && s[i + 1] == q) { sb.Append(q); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                list.Add(new Tok(T.String, sb.ToString(), start, sb.ToString()));
                continue;
            }
            if (c is '=' or '!' or '<' or '>')
            {
                string op;
                if (i + 1 < s.Length && s[i + 1] == '=') { op = s.Substring(i, 2); i += 2; }
                else if (c == '<' && i + 1 < s.Length && s[i + 1] == '>') { op = "!="; i += 2; }
                else { op = c.ToString(); i++; }
                if (op == "!") throw new QuerySyntaxException("Unexpected '!'", start);
                if (op == "==") op = "=";
                list.Add(new Tok(T.Op, op, start));
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                i++;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '.' or '_' || (s[i] is '+' or '-' && s[i - 1] is 'e' or 'E'))) i++;
                var txt = s[start..i].Replace("_", "");
                object val;
                if (long.TryParse(txt, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) val = l;
                else if (ulong.TryParse(txt, NumberStyles.None, CultureInfo.InvariantCulture, out var ul)) val = ul;
                else if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) val = d;
                else { list.Add(new Tok(T.Ident, s[start..i], start)); continue; } // e.g. a hex id starting with a digit
                list.Add(new Tok(T.Number, s[start..i], start, val));
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '@' or '`')
            {
                if (c == '`')
                {
                    var end = s.IndexOf('`', i + 1);
                    if (end < 0) throw new QuerySyntaxException("Unterminated `identifier`", start);
                    list.Add(new Tok(T.Ident, s[(i + 1)..end], start, "quoted"));
                    i = end + 1;
                    continue;
                }
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '-' or '@' or '/' or ':')) i++;
                list.Add(new Tok(T.Ident, s[start..i], start));
                continue;
            }
            throw new QuerySyntaxException($"Unexpected character '{c}'", i);
        }
        list.Add(new Tok(T.End, "<end>", s.Length));
        return list;
    }

    private Tok Peek => _toks[_i];
    private Tok Next() => _toks[_i++];
    private bool IsKeyword(Tok t, string kw) => t.Type == T.Ident && t.Value is null && string.Equals(t.Text, kw, StringComparison.OrdinalIgnoreCase);

    private void Enter()
    {
        if (++_depth > MaxDepth) throw new QuerySyntaxException("Query nesting too deep", Peek.Pos);
    }

    private Expr ParseOr()
    {
        Enter();
        var left = ParseAnd();
        while (IsKeyword(Peek, "OR")) { Next(); left = new OrExpr(left, ParseAnd()); }
        _depth--;
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseUnary();
        while (IsKeyword(Peek, "AND")) { Next(); left = new AndExpr(left, ParseUnary()); }
        return left;
    }

    private Expr ParseUnary()
    {
        if (IsKeyword(Peek, "NOT")) { Next(); Enter(); var e = new NotExpr(ParseUnary()); _depth--; return e; }
        if (Peek.Type == T.LParen)
        {
            Next();
            var e = ParseOr();
            Expect(T.RParen, ")");
            return e;
        }
        if (IsKeyword(Peek, "EXISTS"))
        {
            Next();
            Expect(T.LParen, "(");
            var f = ParseField();
            Expect(T.RParen, ")");
            return new ExistsExpr(f);
        }
        if (IsKeyword(Peek, "TRUE")) { Next(); return TrueExpr.Instance; }
        return ParsePredicate();
    }

    private void Expect(T type, string text)
    {
        var t = Next();
        if (t.Type != type) throw new QuerySyntaxException($"Expected '{text}' but found '{t.Text}'", t.Pos);
    }

    private FieldRef ParseField()
    {
        var t = Next();
        if (t.Type != T.Ident) throw new QuerySyntaxException($"Expected a field name but found '{t.Text}'", t.Pos);
        if (t.Value is "quoted") return new FieldRef(BuiltinField.Attribute, t.Text);
        return ResolveField(t.Text, t.Pos);
    }

    public static FieldRef ResolveField(string name, int pos = 0)
    {
        var lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "time": case "timestamp": case "@t": return new FieldRef(BuiltinField.Time);
            case "level": case "severity": case "@l": return new FieldRef(BuiltinField.Severity);
            case "event": case "event_name": case "name": return new FieldRef(BuiltinField.Event);
            case "event_id": return new FieldRef(BuiltinField.EventId);
            case "service": case "service.name": return new FieldRef(BuiltinField.Service);
            case "trace_id": case "traceid": return new FieldRef(BuiltinField.TraceId);
            case "span_id": case "spanid": return new FieldRef(BuiltinField.SpanId);
            case "operation_id": return new FieldRef(BuiltinField.OperationId);
            case "parent_operation_id": return new FieldRef(BuiltinField.ParentOperationId);
            case "correlation_id": return new FieldRef(BuiltinField.CorrelationId);
            case "body": case "message": case "@m": return new FieldRef(BuiltinField.Body);
            case "seq": case "sequence": return new FieldRef(BuiltinField.Sequence);
            case "exception.type": return new FieldRef(BuiltinField.ExceptionType);
            case "exception.message": return new FieldRef(BuiltinField.ExceptionMessage);
            case "text": return new FieldRef(BuiltinField.Text);
        }
        if (lower.StartsWith("ctx.", StringComparison.Ordinal) && name.Length > 4) return new FieldRef(BuiltinField.Context, name[4..]);
        if (lower.StartsWith("attr.", StringComparison.Ordinal) && name.Length > 5) return new FieldRef(BuiltinField.Attribute, name[5..]);
        // Alias matching the JSON output ("attributes": {...}); an easy mistake otherwise silently matches nothing.
        if (lower.StartsWith("attributes.", StringComparison.Ordinal) && name.Length > 11) return new FieldRef(BuiltinField.Attribute, name[11..]);
        if (name.Length == 0 || name.Length > 256) throw new QuerySyntaxException("Invalid field name", pos);
        return new FieldRef(BuiltinField.Attribute, name);
    }

    public static string BuiltinName(BuiltinField f) => f switch
    {
        BuiltinField.Time => "time",
        BuiltinField.Severity => "level",
        BuiltinField.Event => "event",
        BuiltinField.EventId => "event_id",
        BuiltinField.Service => "service",
        BuiltinField.TraceId => "trace_id",
        BuiltinField.SpanId => "span_id",
        BuiltinField.OperationId => "operation_id",
        BuiltinField.ParentOperationId => "parent_operation_id",
        BuiltinField.CorrelationId => "correlation_id",
        BuiltinField.Body => "body",
        BuiltinField.Sequence => "seq",
        BuiltinField.ExceptionType => "exception.type",
        BuiltinField.ExceptionMessage => "exception.message",
        BuiltinField.Text => "text",
        _ => f.ToString(),
    };

    private Expr ParsePredicate()
    {
        var field = ParseField();
        var t = Peek;
        if (t.Type == T.Op)
        {
            Next();
            var op = t.Text switch
            {
                "=" => CompareOp.Eq,
                "!=" => CompareOp.Ne,
                "<" => CompareOp.Lt,
                "<=" => CompareOp.Le,
                ">" => CompareOp.Gt,
                ">=" => CompareOp.Ge,
                _ => throw new QuerySyntaxException($"Unknown operator '{t.Text}'", t.Pos),
            };
            return new CompareExpr(field, op, ParseLiteral());
        }
        if (IsKeyword(t, "IN"))
        {
            Next();
            Expect(T.LParen, "(");
            var values = new List<Literal> { ParseLiteral() };
            while (Peek.Type == T.Comma)
            {
                Next();
                values.Add(ParseLiteral());
                if (values.Count > MaxInValues) throw new QuerySyntaxException($"IN list longer than {MaxInValues}", Peek.Pos);
            }
            Expect(T.RParen, ")");
            return new InExpr(field, values);
        }
        if (IsKeyword(t, "CONTAINS") || t.Text == "~")
        {
            Next();
            var lit = ParseLiteral();
            return new ContainsExpr(field, lit.Raw?.ToString() ?? "");
        }
        if (IsKeyword(t, "STARTSWITH"))
        {
            Next();
            var lit = ParseLiteral();
            return new StartsWithExpr(field, lit.Raw?.ToString() ?? "");
        }
        if (IsKeyword(t, "IS"))
        {
            Next();
            var negate = false;
            if (IsKeyword(Peek, "NOT")) { Next(); negate = true; }
            if (!IsKeyword(Peek, "NULL")) throw new QuerySyntaxException("Expected NULL after IS", Peek.Pos);
            Next();
            Expr e = new ExistsExpr(field);
            return negate ? e : new NotExpr(e);
        }
        throw new QuerySyntaxException($"Expected an operator after '{field}' but found '{t.Text}'", t.Pos);
    }

    private Literal ParseLiteral()
    {
        var t = Next();
        switch (t.Type)
        {
            case T.String: return new Literal(t.Value, t.Text);
            case T.Number: return new Literal(t.Value, t.Text);
            case T.Ident:
                if (IsKeyword(t, "NULL")) return new Literal(null, "null");
                if (IsKeyword(t, "TRUE")) return new Literal(true, "true");
                if (IsKeyword(t, "FALSE")) return new Literal(false, "false");
                return new Literal(t.Text, t.Text); // bare words such as ERROR or hex ids
            default: throw new QuerySyntaxException($"Expected a value but found '{t.Text}'", t.Pos);
        }
    }
}
