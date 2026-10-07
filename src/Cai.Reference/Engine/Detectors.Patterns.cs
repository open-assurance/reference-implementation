using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>
/// The narrow shape detectors: X6 X7 X9 X13 X16 X18 X19 X20 X21 X22 X25 X26 X27 X28 X29 X30 X32, plus null-dereference
/// (X5) and the security-adjacent X14 X15 X17 X24. Each is written from its catalog text; each fires on one shape.
/// </summary>
public static class Patterns
{
    private static readonly string[] Dims = { "X6", "X7", "X9", "X13", "X16", "X18", "X19", "X20", "X21", "X22", "X25", "X26", "X27", "X28", "X29", "X30", "X32", "X14", "X15", "X17", "X24" };

    public static void Run(ScanContext ctx)
    {
        if (!ctx.Workspace.ProductionTrees.Any()) { foreach (var d in Dims) ctx.Skip(d, "no production C# source to measure"); return; }
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot(); var model = p.Model(tree);
            var usesJsonLib = p.Project.HasPackage("Newtonsoft.Json") || p.Project.HasPackage("System.Text.Json") || root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Name?.ToString() is "System.Text.Json" or "Newtonsoft.Json" or "System.Xml" or "System.Xml.Linq") || true;
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax inv: Invocation(ctx, rel, model, inv, usesJsonLib); break;
                    case IfStatementSyntax ifs: If(ctx, rel, model, ifs); break;
                    case ConditionalExpressionSyntax cond when cond.Condition is InvocationExpressionSyntax tp && Cs.MemberName(tp.Expression) == "TryParse" && (IsConstantLike(cond.WhenFalse) || IsConstantLike(cond.WhenTrue)):
                        break; // `TryParse(..) ? v : default` is a common idiom; only the statement form with no diagnostics is flagged (X7)
                    case WhileStatementSyntax wh: Truncation(ctx, rel, wh); break;
                    case WhenClauseSyntax when_:
                        if (when_.Condition.DescendantNodesAndSelf().Any(n => n is PrefixUnaryExpressionSyntax pu && (pu.IsKind(SyntaxKind.PreIncrementExpression) || pu.IsKind(SyntaxKind.PreDecrementExpression)) || n is PostfixUnaryExpressionSyntax po && (po.IsKind(SyntaxKind.PostIncrementExpression) || po.IsKind(SyntaxKind.PostDecrementExpression)) || n is AssignmentExpressionSyntax))
                            ctx.Add(new Finding("side-effect-in-conditional-guard", "X21", $"`when` guard mutates state while pattern-matching: {Trunc(when_.Condition.ToString(), 70)}", rel, Cs.Line(when_), null, 1));
                        break;
                    case CommonForEachStatementSyntax fe: ForEach(ctx, rel, model, fe); break;
                    case ForStatementSyntax fs: For(ctx, rel, fs); break;
                    case TypeDeclarationSyntax td when td is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax: Type(ctx, rel, model, td); break;
                    case MethodDeclarationSyntax md: Method(ctx, rel, model, md); break;
                    case AnonymousFunctionExpressionSyntax lambda: Callback(ctx, rel, model, lambda); break;
                    case InterpolatedStringExpressionSyntax interp: Markup(ctx, rel, model, interp); break;
                    case TryStatementSyntax ts: TryFinally(ctx, rel, ts); break;
                    case ObjectCreationExpressionSyntax oc when Cs.Simple(oc.Type) == "ProcessStartInfo": ProcessStreams(ctx, rel, oc); break;
                    case BinaryExpressionSyntax bin when (bin.IsKind(SyntaxKind.LogicalOrExpression) || bin.IsKind(SyntaxKind.LogicalAndExpression)) && !(bin.Parent is BinaryExpressionSyntax pb && pb.IsKind(bin.Kind())): Subsumed(ctx, rel, bin); break;
                }
            }
        }
        foreach (var d in Dims)
        {
            var findings = ctx.FindingsFor(d).ToList();
            ctx.Measure(d, Shape.FromFindings(findings, ctx.ProductionKloc), note: $"{findings.Count} site(s)");
        }
    }

    private static string Trunc(string s, int n) { s = Regex.Replace(s, @"\s+", " "); return s.Length <= n ? s : s[..n] + "…"; }
    private static bool IsConstantLike(ExpressionSyntax e) => e is LiteralExpressionSyntax or DefaultExpressionSyntax || e is MemberAccessExpressionSyntax m && (m.Name.Identifier.Text is "Empty" or "MinValue" or "MaxValue" or "Zero");

    // ---- invocations: X6 hand-rolled parsing, X13 process streams, X32 type by simple name, X20 argument guards, X15 unvalidated length, X14 address classification
    private static readonly Regex JsonShape = new(@"\\""?\w+\\""?\\?s\*:|""\\s\*:|\\""[A-Za-z_]+\\""\s*\\s\*:|<\\?/?\w+>|\[\\?""", RegexOptions.Compiled);
    private static readonly Regex JsonShape2 = new(@"\\""[A-Za-z_]+\\""|""[A-Za-z_]+""\s*\\s\*:|<[A-Za-z]+>|</[A-Za-z]+>", RegexOptions.Compiled);

    private static void Invocation(ScanContext ctx, string rel, SemanticModel model, InvocationExpressionSyntax inv, bool usesJsonLib)
    {
        var name = Cs.MemberName(inv.Expression);
        var receiver = Cs.ReceiverText(inv.Expression);
        // X6: a regex (or IndexOf/Split surgery) over JSON/XML text, when the project already has a real parser
        if (receiver == "Regex" && name is "Match" or "Matches" or "IsMatch" or "Replace" && inv.ArgumentList.Arguments.Count >= 2)
        {
            var pattern = inv.ArgumentList.Arguments[1].Expression.ToString();
            if (LooksLikeJsonOrXmlPattern(pattern) && usesJsonLib)
                ctx.Add(new Finding("hand-rolled-structured-format-parsing", "X6", $"Regex over a JSON/XML body ({Trunc(pattern, 50)}) instead of the parser the project already references", rel, Cs.Line(inv), null, 1));
        }
        // X32: AppDomain.GetAssemblies().SelectMany(GetTypes).First(t => t.Name == …)
        if (name is "FirstOrDefault" or "First" or "Single" or "SingleOrDefault" or "Where" && inv.Expression is MemberAccessExpressionSyntax chain && chain.Expression.ToString().Contains("GetAssemblies()") && chain.Expression.ToString().Contains("GetTypes") && inv.ArgumentList.ToString().Contains(".Name =="))
            ctx.Add(new Finding("type-lookup-by-simple-name", "X32", "type resolved by simple name across every loaded assembly: the winner depends on assembly load order", rel, Cs.Line(inv), null, 1));
        // X15: a length read from the stream being parsed sizes an allocation or a read
        if (inv.Expression is MemberAccessExpressionSyntax rm && rm.Name.Identifier.Text is "ReadInt32" or "ReadInt16" or "ReadUInt16" or "ReadUInt32" or "ReadInt64" or "Read7BitEncodedInt")
        {
            var method = Cs.EnclosingMethod(inv);
            if (method is not null && inv.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax decl })
            {
                var v = decl.Identifier.Text;
                var body = Cs.BodyOf(method) ?? method;
                var sized = body.DescendantNodes().Any(n => n is ArrayCreationExpressionSyntax ac && ac.Type.RankSpecifiers.Any(r => r.Sizes.Any(sz => sz.ToString() == v)) || n is InvocationExpressionSyntax rb && Cs.MemberName(rb.Expression) is "ReadBytes" or "ReadChars" && rb.ArgumentList.Arguments.Any(a => a.Expression.ToString() == v) || n is ObjectCreationExpressionSyntax oc && Cs.Simple(oc.Type) is "List" or "Dictionary" or "MemoryStream" && oc.ArgumentList?.Arguments.Any(a => a.Expression.ToString() == v) == true);
                var bounded = body.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString().Contains(v) && Regex.IsMatch(i.Condition.ToString(), $@"\b{Regex.Escape(v)}\s*(>|>=|<|<=)")) || body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c => c.Expression.ToString().Contains("ThrowIf") && c.ArgumentList.ToString().Contains(v)) || body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c => Cs.MemberName(c.Expression) is "Min" or "Clamp" && c.ArgumentList.ToString().Contains(v));
                if (sized && !bounded) ctx.Add(new Finding("unbounded-allocation-from-untrusted-length", "X15", $"`{v}` is read from the input and sizes an allocation or read with no bound: the input chooses the allocation", rel, Cs.Line(inv), null, 1));
            }
        }
    }

    private static bool LooksLikeJsonOrXmlPattern(string pattern) =>
        pattern.Contains("\\\"") && pattern.Contains(":") || Regex.IsMatch(pattern, @"<\\?/?[A-Za-z]+[\\>]") || pattern.Contains("\\s*:\\s*");

    // ---- if statements: X20 wrong guard, X28 index outside guard, X30 negated-or guard, X7 TryParse fallback, X29 handled in For
    private static void If(ScanContext ctx, string rel, SemanticModel model, IfStatementSyntax ifs)
    {
        var cond = ifs.Condition;
        var condText = cond.ToString();
        // X20: a guard whose condition tests emptiness but throws ArgumentNullException
        var throws = (ifs.Statement is BlockSyntax b ? b.Statements.FirstOrDefault() : ifs.Statement) as ThrowStatementSyntax;
        if (throws?.Expression is ObjectCreationExpressionSyntax oc && Cs.Simple(oc.Type) == "ArgumentNullException" && Regex.IsMatch(condText, @"\.(Count|Length)\s*==\s*0|IsNullOrEmpty|IsNullOrWhiteSpace|!\s*\w+\.Any\(\)|\.Count\s*<\s*1|\.Length\s*<\s*1"))
            ctx.Add(new Finding("argument-guard-tests-wrong-condition", "X20", $"guard `{Trunc(condText, 60)}` rejects an EMPTY value but reports ArgumentNullException", rel, Cs.Line(throws), null, 1));
        // X28: `x.Length > 0 && x[0] == a || x[0] == b` — the second index access sits outside the guard
        if (cond is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression } or_)
        {
            var guarded = Guarded(or_.Left);
            if (guarded is not null && Unparenthesize(or_.Right).DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>().Any(e => e.Expression.ToString() == guarded))
                ctx.Add(new Finding("index-access-outside-bounds-guard", "X28", $"`{Trunc(condText, 70)}`: the emptiness guard on {guarded} covers only the left operand; the right `{guarded}[…]` runs on an empty value", rel, Cs.Line(ifs), null, 1));
        }
        // X30: !(flagA || flagB || x != k): De Morgan turns a mixed guard inside out
        if (Unparenthesize(cond) is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not_ && Unparenthesize(not_.Operand) is BinaryExpressionSyntax inner && inner.IsKind(SyntaxKind.LogicalOrExpression))
        {
            var ops = new List<ExpressionSyntax>(); FlattenOr(inner, ops);
            var hasFlag = ops.Any(o => o is IdentifierNameSyntax || o is MemberAccessExpressionSyntax || o is InvocationExpressionSyntax);
            var hasFault = ops.Any(o => o is BinaryExpressionSyntax be && (be.IsKind(SyntaxKind.NotEqualsExpression) || be.IsKind(SyntaxKind.EqualsExpression)));
            if (ops.Count >= 2 && hasFlag && hasFault)
                ctx.Add(new Finding("contradictory-support-guard", "X30", $"`{Trunc(condText, 70)}` mixes capability flags with a fault comparison under one negated OR: by De Morgan it admits what it means to reject", rel, Cs.Line(ifs), null, 1));
        }
        // X7: a failed TryParse / lookup silently becomes a constant, with no log and no throw
        if (Unparenthesize(cond) is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } neg && Unparenthesize(neg.Operand) is InvocationExpressionSyntax tp && Cs.MemberName(tp.Expression) is "TryParse" or "TryGetValue" or "TryParseExact")
        {
            var stmts = ifs.Statement is BlockSyntax bb ? bb.Statements.ToList() : new List<StatementSyntax> { ifs.Statement };
            if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax rs && rs.Expression is not null && IsConstantLike(rs.Expression) && rs.Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } && !IsBoolLiteral(rs.Expression))
                ctx.Add(new Finding("silent-error-fallback", "X7", $"when {Cs.MemberName(tp.Expression)} fails the value silently becomes `{rs.Expression}`: a data error turned into a behaviour change, with no log and no throw", rel, Cs.Line(ifs), null, 1));
        }
    }

    private static bool IsBoolLiteral(ExpressionSyntax e) => e is LiteralExpressionSyntax l && (l.IsKind(SyntaxKind.TrueLiteralExpression) || l.IsKind(SyntaxKind.FalseLiteralExpression));
    private static ExpressionSyntax Unparenthesize(ExpressionSyntax e) { while (e is ParenthesizedExpressionSyntax p) e = p.Expression; return e; }
    private static void FlattenOr(ExpressionSyntax e, List<ExpressionSyntax> ops) { e = Unparenthesize(e); if (e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.LogicalOrExpression)) { FlattenOr(b.Left, ops); FlattenOr(b.Right, ops); } else ops.Add(e); }
    /// <summary>If `e` is `x.Length > 0 && …` / `x.Count > 0 && …` / `x.Any() && …` returns x.</summary>
    private static string? Guarded(ExpressionSyntax e)
    {
        e = Unparenthesize(e);
        if (e is not BinaryExpressionSyntax and_ || !and_.IsKind(SyntaxKind.LogicalAndExpression)) return null;
        var left = Unparenthesize(and_.Left);
        var m = Regex.Match(left.ToString(), @"^([\w.]+)\.(Length|Count)\s*(>\s*0|>=\s*1|!=\s*0)$");
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(left.ToString(), @"^([\w.]+)\.Any\(\)$");
        return m2.Success ? m2.Groups[1].Value : null;
    }

    // ---- X16: a loop that shortens a string until it fits, with no floor
    private static void Truncation(ScanContext ctx, string rel, WhileStatementSyntax wh)
    {
        var body = wh.Statement;
        foreach (var asg in body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
        {
            var target = asg.Left.ToString();
            var rhs = asg.Right.ToString().Replace(" ", "");
            var shortens = Regex.IsMatch(rhs, $@"^{Regex.Escape(target)}\.Substring\(0,{Regex.Escape(target)}\.Length-\d+\)$|^{Regex.Escape(target)}\[\.\.\^\d+\]$|^{Regex.Escape(target)}\.Remove\({Regex.Escape(target)}\.Length-\d+\)$|^{Regex.Escape(target)}\[\.\.\({Regex.Escape(target)}\.Length-\d+\)\]$");
            if (!shortens) continue;
            var cond = wh.Condition.ToString();
            var floored = Regex.IsMatch(cond, $@"{Regex.Escape(target)}\.Length\s*(>|>=)\s*\d+|{Regex.Escape(target)}\.Length\s*>\s*\w+|\w+\s*<\s*{Regex.Escape(target)}\.Length") || body.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString().Contains($"{target}.Length") && i.Statement.DescendantNodesAndSelf().Any(n => n is BreakStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax));
            if (!floored) ctx.Add(new Finding("unbounded-truncation-loop", "X16", $"`while ({Trunc(cond, 50)})` shortens {target} with no floor: an unfittable value grinds down to empty and then to a negative length", rel, Cs.Line(wh), null, 1));
        }
    }

    // ---- X27: the enumerated collection is mutated in the loop body
    private static readonly HashSet<string> Mutators = new(StringComparer.Ordinal) { "Add", "Remove", "RemoveAt", "Insert", "Clear", "AddRange", "RemoveAll", "RemoveRange", "Enqueue", "Dequeue", "Push", "Pop" };
    private static void ForEach(ScanContext ctx, string rel, SemanticModel model, CommonForEachStatementSyntax fe)
    {
        var src = fe.Expression.ToString();
        if (src.EndsWith(".Keys") || src.EndsWith(".Values") || src.Contains(".ToList()") || src.Contains(".ToArray()")) return; // snapshots and dictionary key views (.NET Core 3+) are safe
        var t = Cs.TypeOf(model, fe.Expression);
        var typeName = t?.TypeKind == TypeKind.Error ? "" : t?.Name ?? "";
        if (typeName.StartsWith("Concurrent") || typeName.StartsWith("Immutable") || typeName.Contains("Dictionary")) return;
        foreach (var inv in fe.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma && Mutators.Contains(ma.Name.Identifier.Text) && ma.Expression.ToString() == src)
            {
                if (inv.Ancestors().TakeWhile(a => a != fe).Any(a => a is BreakStatementSyntax) ) continue;
                var followedByBreak = inv.Ancestors().OfType<StatementSyntax>().TakeWhile(s => s != fe.Statement).Any(s => s is BlockSyntax blk && blk.Statements.Any(st => st is BreakStatementSyntax or ReturnStatementSyntax));
                if (followedByBreak) continue;
                ctx.Add(new Finding("collection-modified-during-enumeration", "X27", $"`{src}.{ma.Name.Identifier.Text}(…)` (line {Cs.Line(inv)}) inside `foreach` over {src} invalidates the enumerator", rel, Cs.Line(fe), null, 1));
                break;
            }
        }
    }

    // ---- X29: per-element decision read from a fixed element
    private static void For(ScanContext ctx, string rel, ForStatementSyntax fs)
    {
        var loopVar = fs.Declaration?.Variables.FirstOrDefault()?.Identifier.Text; if (loopVar is null) return;
        foreach (var ifs in fs.Statement.DescendantNodes().OfType<IfStatementSyntax>())
        {
            var fixedAccess = ifs.Condition.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>().FirstOrDefault(e => e.ArgumentList.Arguments.Count == 1 && e.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax);
            if (fixedAccess is null) continue;
            var coll = fixedAccess.Expression.ToString();
            var perElement = ifs.Statement.DescendantNodes().OfType<ElementAccessExpressionSyntax>().Any(e => e.Expression.ToString() == coll && e.ArgumentList.Arguments.Count == 1 && e.ArgumentList.Arguments[0].Expression.ToString() == loopVar);
            if (perElement) ctx.Add(new Finding("loop-decision-on-fixed-element", "X29", $"`{Trunc(ifs.Condition.ToString(), 50)}` decides for every {coll}[{loopVar}] from {coll}[{fixedAccess.ArgumentList.Arguments[0]}]", rel, Cs.Line(ifs), null, 1));
        }
    }

    // ---- types: X18 disposal ownership, X25 inert knob, X26 callback handoff (fields), null-dereference, X17 recursion
    private static readonly Regex DisposableName = new(@"Timer|Semaphore|CancellationTokenSource|Stream|Reader|Writer|HttpClient|Socket|Process|Watcher|Mutex|WaitHandle|Connection|Client|Channel|Handle|Registration|Subscription|Listener", RegexOptions.Compiled);

    private static void Type(ScanContext ctx, string rel, SemanticModel model, TypeDeclarationSyntax td)
    {
        var dispose = td.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text is "Dispose" or "DisposeAsync" && m.ParameterList.Parameters.Count == 0);
        var fields = td.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables.Select(v => (field: f, v))).ToList();
        var ctors = td.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (dispose is not null)
        {
            var disposeText = (Cs.BodyOf(dispose)?.ToString() ?? "") + string.Join(" ", td.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "Dispose" && m.ParameterList.Parameters.Count == 1).Select(m => Cs.BodyOf(m)?.ToString()));
            foreach (var (f, v) in fields)
            {
                var name = v.Identifier.Text;
                var typeName = Cs.Simple(f.Declaration.Type);
                var tsym = Cs.SymbolOf(model, f.Declaration.Type) as ITypeSymbol;
                var isDisposable = tsym is not null && tsym.TypeKind != TypeKind.Error ? tsym.AllInterfaces.Any(i => i.Name is "IDisposable" or "IAsyncDisposable") || tsym.Name is "IDisposable" : DisposableName.IsMatch(typeName);
                if (!isDisposable) continue;
                var owned = v.Initializer?.Value is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                    || ctors.Any(c => c.Body?.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.Left.ToString().TrimStart('t', 'h', 'i', 's', '.') == name && a.Right is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" or "Open" or "OpenRead" or "OpenWrite" or "CreateClient" } }) == true);
                var handed = ctors.Any(c => c.Body?.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.Left.ToString().TrimStart('t', 'h', 'i', 's', '.') == name && a.Right is IdentifierNameSyntax id && c.ParameterList.Parameters.Any(pp => pp.Identifier.Text == id.Identifier.Text)) == true);
                var disposed = Regex.IsMatch(disposeText, $@"\b{Regex.Escape(name)}\s*\??\.\s*Dispose(Async)?\s*\(") || Regex.IsMatch(disposeText, $@"\b{Regex.Escape(name)}\s*\??\.\s*(Close|Cancel)\s*\(") ;
                if (owned && !disposed) ctx.Add(new Finding("improper-resource-disposal", "X18", $"{td.Identifier.Text} owns {typeName} {name} (created here) but Dispose never releases it", rel, Cs.Line(v), null, 1));
                else if (handed && !owned && disposed && typeName is "HttpClient" or "ILogger" or "DbContext" or "DbConnection") ctx.Add(new Finding("improper-resource-disposal", "X18", $"{td.Identifier.Text} disposes {name}, which it was handed and does not own", rel, Cs.Line(v), null, 0.5));
            }
        }
        // X25: a constructor parameter stored in a field nothing reads, while its default is spelled out again where it should have been read
        foreach (var ctor in ctors)
        {
            foreach (var asg in ctor.Body?.DescendantNodes().OfType<AssignmentExpressionSyntax>() ?? Enumerable.Empty<AssignmentExpressionSyntax>())
            {
                var fieldName = asg.Left.ToString().Replace("this.", "");
                if (!fields.Any(f => f.v.Identifier.Text == fieldName)) continue;
                var defaultExpr = asg.Right is BinaryExpressionSyntax co && co.IsKind(SyntaxKind.CoalesceExpression) ? co.Right : null;
                if (defaultExpr is null) continue;
                var reads = td.DescendantTokens().Count(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == fieldName) - 2; // declaration + this write
                if (reads > 0) continue;
                var defaultText = Regex.Replace(defaultExpr.ToString(), @"\s+", "");
                if (defaultText.Length < 6) continue;
                var again = td.DescendantNodes().OfType<ExpressionSyntax>().FirstOrDefault(e => e != defaultExpr && !e.Ancestors().Contains(ctor) && Regex.Replace(e.ToString(), @"\s+", "") == defaultText);
                if (again is not null) ctx.Add(new Finding("inert-configuration-option", "X25", $"{fieldName} stores a constructor option nobody reads; its default `{Trunc(defaultExpr.ToString(), 40)}` is spelled out again here, so every caller who supplies a value silently gets the default", rel, Cs.Line(again), null, 1));
            }
        }
        // X17: a recursive walk over a caller-supplied JSON/XML node with no depth parameter
        foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
        {
            // System.Text.Json and Json.NET bound nesting at parse time (MaxDepth 64) unless raised; XML and YAML readers do not
            var jsonUnbounded = Regex.IsMatch(td.SyntaxTree.GetRoot().ToString() + string.Concat(ctx.Workspace.ProductionTrees.Select(t => t.tree.GetRoot().ToString()).Where(s => s.Contains("MaxDepth"))), @"MaxDepth\s*=\s*(0|[1-9]\d{2,})");
            var docParam = m.ParameterList.Parameters.FirstOrDefault(pp => Cs.Simple(pp.Type) is "XElement" or "XmlNode" or "XmlElement" or "XNode" or "XDocument" or "XContainer" or "YamlNode" or "YamlMappingNode" || jsonUnbounded && Cs.Simple(pp.Type) is "JsonElement" or "JsonNode" or "JObject" or "JToken" or "JArray");
            if (docParam is null || m.Body is null) continue;
            var recursiveCall = m.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Cs.MemberName(i.Expression) == m.Identifier.Text && (i.Expression is IdentifierNameSyntax || i.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }));
            if (recursiveCall is null) continue;
            var depthGuard = m.ParameterList.Parameters.Any(pp => Regex.IsMatch(pp.Identifier.Text, "depth|level|remaining|budget", RegexOptions.IgnoreCase)) || m.Body.ToString().Contains("MaxDepth") || Regex.IsMatch(m.Body.ToString(), @"[Dd]epth\s*[<>]") || Regex.IsMatch(td.ToString(), @"MaxDepth|[Dd]epth\s*(>|>=|<|<=)");
            if (!depthGuard) ctx.Add(new Finding("uncontrolled-recursion", "X17", $"{m.Identifier.Text} recurses over the caller-supplied {Cs.Simple(docParam.Type)} with no depth bound: the document's nesting chooses the stack depth", rel, Cs.Line(recursiveCall), null, 1));
        }
        // X14: a hand-rolled public/private address check that unwraps IPv4-mapped IPv6 but not the other IPv4-in-IPv6 embeddings
        foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
        {
            var text = m.ToString();
            if (!(text.Contains("IPAddress") && (Regex.IsMatch(text, "IsLoopback|IsPrivate|IsPublic|Private|Internal|IsIPv4MappedToIPv6|MapToIPv4") ))) continue;
            if (!text.Contains("MapToIPv4") && !text.Contains("IsIPv4MappedToIPv6")) continue;
            var handlesOthers = text.Contains("2002:") || Regex.IsMatch(text, "6to4|NAT64|64:ff9b|IPv4Compatible|::ffff:0:0|Teredo|2001:0?:", RegexOptions.IgnoreCase);
            // the IPv6 branch must end by ADMITTING the address for the gap to matter; a check that refuses every other IPv6 address has no gap
            var v6If = m.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault(i => i.Condition.ToString().Contains("InterNetworkV6"));
            var branch = v6If?.Statement.ToString() ?? "";
            var admits = Regex.IsMatch(branch, @"return\s+true|return\s+\w*(Public|Allowed|Ok)\b|IsIPv6(LinkLocal|SiteLocal|UniqueLocal)") && !Regex.IsMatch(branch.Replace(" ", ""), @"return(false|\w*(Private|Reject|Deny|Blocked))[;)]\s*}\s*$");
            if (!handlesOthers && v6If is not null && admits) ctx.Add(new Finding("server-side-request-forgery", "X14", $"{m.Identifier.Text} unwraps IPv4-mapped IPv6 addresses but admits every other IPv6 form: 6to4, NAT64 or IPv4-compatible encodings of a private host walk past the check", rel, Cs.Line(v6If), null, 1));
        }
    }

    // ---- methods: null-dereference after a null-conditional, X19 unrestored global state, X26 callback handoff
    private static void Method(ScanContext ctx, string rel, SemanticModel model, MethodDeclarationSyntax md)
    {
        if (md.Body is null) return;
        var body = md.Body;
        // null-dereference: `v?.X` then `v.Y` in the same block with no null test in between
        foreach (var p in md.ParameterList.Parameters)
        {
            var v = p.Identifier.Text;
            var cond = body.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(c => c.Expression is IdentifierNameSyntax id && id.Identifier.Text == v);
            if (cond is null) continue;
            var later = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(ma => ma.Expression is IdentifierNameSyntax id2 && id2.Identifier.Text == v && ma.SpanStart > cond.Span.End && !ma.Ancestors().OfType<IfStatementSyntax>().Any(i => Regex.IsMatch(i.Condition.ToString(), $@"\b{v}\b\s*(!=|is not)\s*null|\b{v}\s+is\s+\{{") && i.Statement.Span.Contains(ma.Span)));
            if (later is null) continue;
            var guardedBetween = body.DescendantNodes().OfType<StatementSyntax>().Any(s => s.SpanStart > cond.Span.End && s.Span.End < later.SpanStart && Regex.IsMatch(s.ToString(), $@"\b{v}\b\s*(==|is)\s*null|ThrowIfNull\({v}\)|\b{v}\s*\?\?="));
            if (!guardedBetween) ctx.Add(new Finding("null-dereference", "X5", $"{v} is treated as possibly null (`{v}?.`) and then dereferenced unconditionally (`{Trunc(later.ToString(), 30)}`)", rel, Cs.Line(later), null, 1));
        }
        // X19: process-global state changed and restored only on the happy path
        foreach (var stmt in body.Statements.Concat(body.DescendantNodes().OfType<StatementSyntax>()).Distinct())
        {
            var text = stmt is ExpressionStatementSyntax es ? es.Expression.ToString() : "";
            var isSet = Regex.IsMatch(text, @"^(Directory\.SetCurrentDirectory|Environment\.CurrentDirectory\s*=|Environment\.SetEnvironmentVariable|CultureInfo\.(Current|DefaultThread)(UI)?Culture\s*=|Thread\.CurrentThread\.Current(UI)?Culture\s*=|Console\.(SetOut|SetError|SetIn))");
            if (!isSet) continue;
            var api = Regex.Match(text, @"^[\w.]+").Value;
            var inTry = stmt.Ancestors().OfType<TryStatementSyntax>().Any(t => t.Finally is not null && t.Block.Span.Contains(stmt.Span) || t.Finally is not null && t.Finally.Block.Span.Contains(stmt.Span));
            var restoredInFinally = body.DescendantNodes().OfType<FinallyClauseSyntax>().Any(f => f.Block.ToString().Contains(api.Split('.').Last().TrimEnd('=')) || f.Block.ToString().Contains(api.Replace(" =", "")));
            var usingScope = stmt.Ancestors().Any(a => a is UsingStatementSyntax);
            if (!inTry && !restoredInFinally && !usingScope)
            {
                var others = body.DescendantNodes().OfType<ExpressionStatementSyntax>().Count(e => e != stmt && e.Expression.ToString().StartsWith(api.Replace(" =", "")));
                if (others > 0) { ctx.Add(new Finding("unrestored-process-global-state", "X19", $"{api} is changed and restored only on the path where nothing throws; an exception leaks the change to the rest of the process", rel, Cs.Line(stmt), null, 1)); break; }
            }
        }
    }

    private static readonly HashSet<string> PlainCollections = new(StringComparer.Ordinal) { "List", "Queue", "Dictionary", "HashSet", "Stack", "LinkedList", "SortedList", "SortedDictionary" };

    // ---- X26: a plain collection written inside an event-handler lambda and read by the body that waits on it
    private static void Callback(ScanContext ctx, string rel, SemanticModel model, AnonymousFunctionExpressionSyntax lambda)
    {
        if (lambda.Parent is not AssignmentExpressionSyntax asg || !asg.IsKind(SyntaxKind.AddAssignmentExpression)) return;   // `x.Event += (…) => …`
        var method = Cs.EnclosingMethod(lambda.Parent); var methodBody = method is null ? null : Cs.BodyOf(method);
        if (methodBody is null) return;
        var locals = methodBody.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Initializer?.Value is ObjectCreationExpressionSyntax oc && PlainCollections.Contains(Cs.Simple(oc.Type)) || v.Initializer?.Value is ImplicitObjectCreationExpressionSyntax && v.Parent is VariableDeclarationSyntax vd && PlainCollections.Contains(Cs.Simple(vd.Type))).Select(v => v.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var fieldNames = Cs.EnclosingType(lambda)?.Members.OfType<FieldDeclarationSyntax>().Where(f => PlainCollections.Contains(Cs.Simple(f.Declaration.Type))).SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)).ToHashSet(StringComparer.Ordinal) ?? new();
        foreach (var inv in lambda.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma || !Mutators.Contains(ma.Name.Identifier.Text)) continue;
            var target = ma.Expression.ToString();
            if (!locals.Contains(target) && !fieldNames.Contains(target)) continue;
            if (inv.Ancestors().TakeWhile(a => a != lambda).Any(a => a is LockStatementSyntax)) continue;
            var readOutside = methodBody.DescendantNodes().Where(n => !lambda.Span.Contains(n.Span)).OfType<MemberAccessExpressionSyntax>().Any(r => r.Expression.ToString() == target);
            if (readOutside) ctx.Add(new Finding("unsynchronized-callback-handoff", "X26", $"{target} is written from a callback and read by the waiting body with no lock: a {Cs.Simple((methodBody.DescendantNodes().OfType<VariableDeclarationSyntax>().FirstOrDefault(v => v.Variables.Any(x => x.Identifier.Text == target))?.Type))} is not safe for that handoff", rel, Cs.Line(inv), null, 1));
        }
    }

    // ---- X24: document text interpolated into generated markup without escaping
    private static readonly Regex MarkupLiteral = new(@"<[a-zA-Z][\w-]*[^>]*=\s*[""']?$|<[a-zA-Z][\w-]*[^>]*>$|^[""']?\s*/?>|^[""'][^<]*</?[a-zA-Z]", RegexOptions.Compiled);
    private static void Markup(ScanContext ctx, string rel, SemanticModel model, InterpolatedStringExpressionSyntax interp)
    {
        var texts = interp.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.Text).ToList();
        var attrPos = texts.Any(t => Regex.IsMatch(t, @"<[a-zA-Z][\w-]*\b[^<]*\b[\w-]+=\s*[""']$") || Regex.IsMatch(t, @"\b[\w-]+=\s*[""']$") && texts.Any(x => x.Contains('<')));
        var elemPos = texts.Any(t => Regex.IsMatch(t, @"<[a-zA-Z][\w-]*[^<]*>$"));
        if (!attrPos && !elemPos) return;
        var method = Cs.EnclosingMethod(interp);
        var inWriter = method is not null && Regex.IsMatch(Cs.BodyOf(method)?.ToString() ?? "", @"StringBuilder|TextWriter|\.Write\(|\.Append\(|Markup|Html");
        if (!inWriter) return;
        foreach (var hole in interp.Contents.OfType<InterpolationSyntax>())
        {
            var text = hole.Expression.ToString();
            if (Regex.IsMatch(text, @"Encode|Escape|Sanitize|Attribute\(|HtmlString|nameof|ToString\(""[^""]*""\)")) continue;
            if (hole.Expression is LiteralExpressionSyntax) continue;
            try { if (model.GetConstantValue(hole.Expression).HasValue) continue; } catch (Exception) { }
            var tsym = Cs.TypeOf(model, hole.Expression);
            if (tsym is not null && tsym.TypeKind != TypeKind.Error && tsym.SpecialType is not (SpecialType.System_String or SpecialType.System_Object or SpecialType.None)) continue;   // numbers and bools cannot carry markup
            ctx.Add(new Finding("cross-site-scripting", "X24", $"`{Trunc(text, 40)}` is interpolated into generated markup {(attrPos ? "inside an attribute" : "as element content")} without escaping", rel, Cs.Line(hole), null, 1));
            break;
        }
    }

    // ---- X13: both standard streams redirected, only one of them drained
    private static void ProcessStreams(ScanContext ctx, string rel, ObjectCreationExpressionSyntax oc)
    {
        var method = Cs.EnclosingMethod(oc); var body = method is null ? null : Cs.BodyOf(method); if (body is null) return;
        var text = body.ToString();
        var redirectOut = Regex.IsMatch(text, @"RedirectStandardOutput\s*=\s*true"); var redirectErr = Regex.IsMatch(text, @"RedirectStandardError\s*=\s*true");
        if (!redirectOut || !redirectErr) return;
        var drainsOut = Regex.IsMatch(text, @"StandardOutput\s*\.\s*(ReadToEnd|ReadToEndAsync|ReadLine|ReadLineAsync|BaseStream|CopyTo|CopyToAsync|ReadAsync|Read)\b|BeginOutputReadLine|OutputDataReceived");
        var drainsErr = Regex.IsMatch(text, @"StandardError\s*\.\s*(ReadToEnd|ReadToEndAsync|ReadLine|ReadLineAsync|BaseStream|CopyTo|CopyToAsync|ReadAsync|Read)\b|BeginErrorReadLine|ErrorDataReceived");
        if (drainsOut != drainsErr) ctx.Add(new Finding("undrained-child-process-stream", "X13", $"both standard streams are redirected but only {(drainsOut ? "stdout" : "stderr")} is read: the child blocks once the unread pipe fills", rel, Cs.Line(oc), null, 1));
    }

    // ---- X9: an operand of a boolean chain implies another operand of the same chain
    private static void Subsumed(ScanContext ctx, string rel, BinaryExpressionSyntax chain)
    {
        var ops = new List<ExpressionSyntax>(); var kind = chain.Kind();
        void Flat(ExpressionSyntax e) { e = Unparenthesize(e); if (e is BinaryExpressionSyntax b && b.IsKind(kind)) { Flat(b.Left); Flat(b.Right); } else ops.Add(e); }
        Flat(chain);
        if (ops.Count < 2) return;
        var texts = ops.Select(o => Regex.Replace(o.ToString(), @"\s+", "")).ToList();
        for (var i = 0; i < ops.Count; i++) for (var j = i + 1; j < ops.Count; j++)
        {
            string? why = null;
            if (texts[i] == texts[j]) why = "is repeated";
            var a = PrefixCall(ops[i]); var b = PrefixCall(ops[j]);
            if (why is null && a is not null && b is not null && a.Value.receiver == b.Value.receiver && a.Value.method == b.Value.method)
            {
                var (shorter, longer) = a.Value.literal.Length <= b.Value.literal.Length ? (a.Value, b.Value) : (b.Value, a.Value);
                var implied = a.Value.method switch { "StartsWith" => longer.literal.StartsWith(shorter.literal, StringComparison.Ordinal), "EndsWith" => longer.literal.EndsWith(shorter.literal, StringComparison.Ordinal), "Contains" => longer.literal.Contains(shorter.literal, StringComparison.Ordinal), _ => false };
                if (implied && shorter.literal != longer.literal) why = $"`{a.Value.method}(\"{longer.literal}\")` already implies `{a.Value.method}(\"{shorter.literal}\")`";
            }
            var ca = Compare(ops[i]); var cb = Compare(ops[j]);
            if (why is null && ca is not null && cb is not null && ca.Value.operand == cb.Value.operand && ca.Value.dir == cb.Value.dir) why = $"comparisons on {ca.Value.operand} in the same direction: one bound subsumes the other";
            if (why is not null) { ctx.Add(new Finding("redundant-condition-operand", "X9", $"`{Trunc(chain.ToString(), 70)}`: operand {why}, so the expression means something other than it reads", rel, Cs.Line(chain), null, 1)); return; }
        }
    }
    private static (string receiver, string method, string literal)? PrefixCall(ExpressionSyntax e)
    {
        if (Unparenthesize(e) is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text is "StartsWith" or "EndsWith" or "Contains" && inv.ArgumentList.Arguments.Count >= 1 && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.Value: string lit })
            return (ma.Expression.ToString(), ma.Name.Identifier.Text, lit);
        return null;
    }
    private static (string operand, char dir)? Compare(ExpressionSyntax e)
    {
        if (Unparenthesize(e) is BinaryExpressionSyntax b && b.Right is LiteralExpressionSyntax { Token.Value: not string } && (b.IsKind(SyntaxKind.GreaterThanExpression) || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression) || b.IsKind(SyntaxKind.LessThanExpression) || b.IsKind(SyntaxKind.LessThanOrEqualExpression)))
            return (b.Left.ToString(), b.IsKind(SyntaxKind.GreaterThanExpression) || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ? '>' : '<');
        return null;
    }

    // ---- X22: a flag-guarded release in `finally` that contradicts the value returned beside it
    private static void TryFinally(ScanContext ctx, string rel, TryStatementSyntax ts)
    {
        if (ts.Finally is null) return;
        var guard = ts.Finally.Block.Statements.OfType<IfStatementSyntax>().FirstOrDefault(i => i.Condition is IdentifierNameSyntax && Regex.IsMatch(i.Statement.ToString(), @"\.(Release|Exit|Dispose|Set|Unlock)\s*\("));
        if (guard is null) return;
        var flag = ((IdentifierNameSyntax)guard.Condition).Identifier.Text;
        // every `return true` in the try block must have cleared the flag first (success keeps the primitive; the guard hands it back)
        var statements = ts.Block.DescendantNodes().OfType<StatementSyntax>().ToList();
        foreach (var ret in ts.Block.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (ret.Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.TrueLiteralExpression)) continue;
            var cleared = statements.Where(s => s.SpanStart < ret.SpanStart).Any(s => s is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax a && a.Left.ToString() == flag && a.Right.IsKind(SyntaxKind.FalseLiteralExpression) && (s.Parent == ret.Parent || s.Parent?.Parent == ts.Block || ret.Ancestors().Contains(s.Parent!)));
            if (!cleared) ctx.Add(new Finding("lock-release-state-mismatch", "X22", $"returns true while `{flag}` is still set, so the `finally` releases the primitive this result claims to hold", rel, Cs.Line(ret), null, 1));
        }
    }
}
