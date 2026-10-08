using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Code Health: D1 D2 D3 R3 D4 X10 D17 GD1 IC1 X1 X2 PF3 X3 X4 X23 X5.</summary>
public static class CodeHealth
{
    public static void Run(ScanContext ctx)
    {
        if (!ctx.Workspace.ProductionTrees.Any())
        {
            foreach (var d in new[] { "D1", "D2", "D3", "R3", "D4", "X10", "D17", "GD1", "IC1", "X1", "X2", "PF3", "X3", "X4", "X23", "X5", "D39" }) ctx.Skip(d, "no production C# source to measure");
            return;
        }
        Complexity(ctx); Size(ctx); Duplication(ctx); Debt(ctx); Async(ctx); Exceptions(ctx); Logging(ctx); Nullable(ctx);
    }

    // ---------- D1 / D2 ----------
    private static void Complexity(ScanContext ctx)
    {
        int methods = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            foreach (var m in Cs.MethodLike(tree.GetRoot()))
            {
                var body = Cs.BodyOf(m); if (body is null) continue;
                methods++;
                var cc = Cyclomatic(body);
                var cog = Cognitive(body);
                var loc = ctx.Loc(Cs.IdentifierOf(m));
                var name = $"{Cs.TypeName(m)}.{Cs.NameOf(m)}";
                if (cc > 15) ctx.Add(new Finding("high-cyclomatic-complexity", "D1", $"{name} has cyclomatic complexity {cc} (threshold 15): decisions counted per branch, with lookup-table switches counted once", loc.File, loc.Line, loc.Line, cc > 30 ? 2 : 1));
                if (cog > 15) ctx.Add(new Finding("high-cognitive-complexity", "D2", $"{name} has cognitive complexity {cog} (threshold 15): nesting-weighted branches, boolean-operator changes and recursion", loc.File, loc.Line, loc.Line, cog > 30 ? 2 : 1));
                // D39: how much IL a body compiles to, estimated from its syntax (packages are not restored, so nothing is compiled to IL here)
                var rel39 = ctx.Workspace.RelPath(tree.FilePath);
                if (!Regex.IsMatch(rel39, @"(?i)/Migrations/|ModelSnapshot|\.Designer\.cs$|\.g\.cs$") && !Regex.IsMatch(name, @"^(Program|Startup)\.|ServiceCollection|\.(Add\w*Services?|Configure\w*|Up|Down|BuildModel|BuildTargetModel)$"))
                {
                    var il = IlProxy(body);
                    if (il > 700) ctx.Add(new Finding("compiled-code-size", "D39", $"{name} compiles to roughly {il} IL instructions by syntactic estimate (threshold 700): {body.DescendantNodes().OfType<InvocationExpressionSyntax>().Count()} calls and {body.DescendantNodes().OfType<LiteralExpressionSyntax>().Count()} literals inlined into one body", loc.File, loc.Line, loc.Line, il > 1400 ? 2 : 1));
                }
            }
        }
        ctx.Facts["methods"] = methods.ToString();
        ctx.Measure("D1", Shape.FromFindings(ctx.FindingsFor("D1"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("D1").Count()} of {methods} methods over the budget");
        ctx.Measure("D39", Shape.FromFindings(ctx.FindingsFor("D39"), ctx.ProductionKloc), confidence: 0.7, note: $"{ctx.FindingsFor("D39").Count()} of {methods} method bodies over ~700 estimated IL instructions (syntactic estimate, not compiled IL)");
        ctx.Measure("D2", Shape.FromFindings(ctx.FindingsFor("D2"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("D2").Count()} of {methods} methods over the budget");
    }

    /// <summary>1 + decision points. A switch whose every section is a flat one-or-two-statement arm (a lookup table) counts once, not per label.</summary>
    /// <summary>A syntactic stand-in for the IL a body compiles to: calls, member loads, literals, operators, allocations and interpolations each cost roughly what they cost the compiler.</summary>
    public static int IlProxy(SyntaxNode body)
    {
        var n = 0;
        foreach (var node in body.DescendantNodes(d => d is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
            n += node switch
            {
                InvocationExpressionSyntax inv => 2 + inv.ArgumentList.Arguments.Count,
                ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax => 3,
                MemberAccessExpressionSyntax or LiteralExpressionSyntax or IdentifierNameSyntax => 1,
                BinaryExpressionSyntax or AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or CastExpressionSyntax => 1,
                InterpolationSyntax => 3,
                ElementAccessExpressionSyntax => 2,
                ConditionalExpressionSyntax or IfStatementSyntax or SwitchSectionSyntax or SwitchExpressionArmSyntax => 2,
                ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax => 1,
                _ => 0,
            };
        return n;
    }

    public static int Cyclomatic(SyntaxNode body)
    {
        var cc = 1;
        foreach (var n in body.DescendantNodes(d => d is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            switch (n)
            {
                case IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax: cc++; break;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.CoalesceExpression): cc++; break;
                case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.CoalesceAssignmentExpression): cc++; break;
                case SwitchStatementSyntax s:
                    cc += IsLookupTable(s) ? 1 : s.Sections.Sum(sec => sec.Labels.Count(l => l is not DefaultSwitchLabelSyntax)); break;
                case SwitchExpressionSyntax se:
                    cc += se.Arms.All(a => a.Expression is LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax or ThrowExpressionSyntax && a.WhenClause is null) ? 1 : se.Arms.Count(a => a.Pattern is not DiscardPatternSyntax); break;
            }
        }
        return cc;
    }

    private static bool IsLookupTable(SwitchStatementSyntax s) => s.Sections.All(sec => sec.Statements.Count <= 2 && sec.Statements.All(st => st is ReturnStatementSyntax or BreakStatementSyntax or ThrowStatementSyntax or ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax or InvocationExpressionSyntax }) && !sec.DescendantNodes().Any(d => d is IfStatementSyntax or SwitchStatementSyntax or ConditionalExpressionSyntax or WhileStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax || d is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression))));

    /// <summary>Sonar-style cognitive complexity: +1 per branch/loop/catch/ternary/goto/recursion, +nesting for nested ones, +1 per boolean-operator sequence change.</summary>
    public static int Cognitive(SyntaxNode body)
    {
        var score = 0;
        var owner = body.Parent;
        var ownerName = owner is not null ? Cs.NameOf(owner) : "";
        void Walk(SyntaxNode node, int nesting)
        {
            foreach (var child in node.ChildNodes())
            {
                switch (child)
                {
                    case IfStatementSyntax ifs:
                        var isElseIf = ifs.Parent is ElseClauseSyntax;
                        score += isElseIf ? 1 : 1 + nesting;
                        Walk(ifs.Condition, nesting); Walk(ifs.Statement, nesting + 1);
                        if (ifs.Else is not null) { if (ifs.Else.Statement is IfStatementSyntax) Walk(ifs.Else, nesting); else { score += 1; Walk(ifs.Else.Statement, nesting + 1); } }
                        continue;
                    case SwitchStatementSyntax or SwitchExpressionSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or CatchClauseSyntax:
                        score += 1 + nesting; Walk(child, nesting + 1); continue;
                    case ConditionalExpressionSyntax ce:
                        score += 1 + nesting; Walk(ce.Condition, nesting); Walk(ce.WhenTrue, nesting + 1); Walk(ce.WhenFalse, nesting + 1); continue;
                    case GotoStatementSyntax: score += 1; continue;
                    case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax: Walk(child, nesting + 1); continue;
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
                        // one increment per sequence of like operators; a change of operator starts a new sequence
                        var kinds = new List<SyntaxKind>(); Flatten(b, kinds, nesting);
                        score += 1; for (var i = 1; i < kinds.Count; i++) if (kinds[i] != kinds[i - 1]) score++;
                        continue;
                    case InvocationExpressionSyntax inv when ownerName.Length > 0 && Cs.MemberName(inv.Expression) == ownerName && inv.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                        score += 1; Walk(child, nesting); continue;
                }
                Walk(child, nesting);
            }
        }
        void Flatten(ExpressionSyntax e, List<SyntaxKind> kinds, int nesting)
        {
            if (e is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)))
            { Flatten(b.Left, kinds, nesting); kinds.Add(b.Kind()); Flatten(b.Right, kinds, nesting); }
            else if (e is ParenthesizedExpressionSyntax pe) Flatten(pe.Expression, kinds, nesting);
            else if (e is PrefixUnaryExpressionSyntax pu && pu.IsKind(SyntaxKind.LogicalNotExpression) && pu.Operand is ParenthesizedExpressionSyntax) { kinds.Add(SyntaxKind.None); Walk(e, nesting); }
            else Walk(e, nesting);
        }
        Walk(body, 0);
        return score;
    }

    // ---------- D3 / R3 ----------
    private static void Size(ScanContext ctx)
    {
        int types = 0, files = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            files++;
            var root = tree.GetRoot();
            var fileLoc = Workspace.CountLoc(tree);
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            if (fileLoc > 800) ctx.Add(new Finding("oversized-source-file", "R3", $"{Path.GetFileName(rel)} has {fileLoc} lines of code (threshold 800)", rel, 1, 1, fileLoc > 1500 ? 2 : 1));
            foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (t is InterfaceDeclarationSyntax) continue;
                types++;
                var methodCount = t.Members.Count(m => m is MethodDeclarationSyntax or ConstructorDeclarationSyntax);
                var fieldCount = t.Members.Count(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax);
                var lines = Cs.Lines(t);
                if (methodCount >= 25 || lines >= 500 && methodCount >= 12)
                {
                    var loc = ctx.Loc(t);
                    ctx.Add(new Finding("god-class", "D3", $"{t.Identifier.Text} has {methodCount} methods, {fieldCount} fields/properties and {lines} lines: a class doing too much", loc.File, loc.Line, loc.EndLine, methodCount >= 50 || lines >= 1000 ? 2 : 1));
                }
            }
            foreach (var m in Cs.MethodLike(root))
            {
                var body = Cs.BodyOf(m); if (body is null) continue;
                var lines = Cs.Lines(body);
                var statements = body.DescendantNodes().OfType<StatementSyntax>().Count(s => s is not BlockSyntax);
                if (lines >= 100 || statements >= 80)
                {
                    var loc = ctx.Loc(Cs.IdentifierOf(m));
                    ctx.Add(new Finding("long-method", "D3", $"{Cs.TypeName(m)}.{Cs.NameOf(m)} is {lines} lines / {statements} statements long (threshold 100 lines or 80 statements)", loc.File, loc.Line, loc.Line, lines >= 200 ? 2 : 1));
                }
            }
        }
        ctx.Facts["types"] = types.ToString();
        ctx.Measure("D3", Shape.FromFindings(ctx.FindingsFor("D3"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("D3").Count()} oversized classes/methods across {types} types");
        ctx.Measure("R3", Shape.FromFindings(ctx.FindingsFor("R3"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("R3").Count()} of {files} files over 800 lines");
    }

    // ---------- D4 / X10 ----------
    private sealed record Tok(string Text, int Line, string File);
    /// <summary>Database-bound terminal operators (EF Core, Marten, Dapper-style): I/O whatever the receiver type resolves to without restored packages.</summary>
    private static readonly Regex QueryTerminal = new(@"^(ToListAsync|ToArrayAsync|CountAsync|LongCountAsync|AnyAsync|AllAsync|FirstAsync|FirstOrDefaultAsync|SingleAsync|SingleOrDefaultAsync|SumAsync|MaxAsync|MinAsync|AverageAsync|ToDictionaryAsync|ExecuteUpdateAsync|ExecuteDeleteAsync|SaveChangesAsync|FindAsync|LoadAsync|ForEachAsync|ContainsAsync|QueryAsync|QueryFirstOrDefaultAsync|ExecuteAsync|ExecuteScalarAsync)$", RegexOptions.Compiled);

    private static void Duplication(ScanContext ctx)
    {
        const int Window = 40, MinClone = 70, MinLines = 7;
        var streams = new List<List<Tok>>();
        var predicates = new Dictionary<string, List<(string file, int line)>>(StringComparer.Ordinal);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            var root = tree.GetRoot();
            var toks = new List<Tok>();
            foreach (var t in root.DescendantTokens())
            {
                if (t.IsKind(SyntaxKind.EndOfFileToken)) continue;
                // data-only shapes are not clones: auto-properties, record parameter lists, usings, attributes, namespace headers
                if (t.Parent?.AncestorsAndSelf().Any(a => a is UsingDirectiveSyntax or AttributeListSyntax or FieldDeclarationSyntax or BaseListSyntax
                        || a is PropertyDeclarationSyntax pd && pd.AccessorList is not null && pd.AccessorList.Accessors.All(acc => acc.Body is null && acc.ExpressionBody is null)
                        || a is ParameterListSyntax pl && pl.Parent is RecordDeclarationSyntax
                        || a is ConstructorDeclarationSyntax cd && (cd.Body is null || cd.Body.Statements.All(st => st is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax or InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ThrowIfNull" or "ThrowIfNullOrEmpty" or "ThrowIfNullOrWhiteSpace" } } }))) == true) continue;
                if (t.Parent is BaseNamespaceDeclarationSyntax || t.Parent is NameSyntax && t.Parent.Parent is BaseNamespaceDeclarationSyntax) continue;
                toks.Add(new Tok(t.Text, Cs.Line(t), rel));
            }
            if (toks.Count >= Window) streams.Add(toks);
            foreach (var cond in root.DescendantNodes().Where(n => n is IfStatementSyntax or WhileStatementSyntax or ConditionalExpressionSyntax).Select(n => n switch { IfStatementSyntax i => i.Condition, WhileStatementSyntax w => w.Condition, ConditionalExpressionSyntax c => c.Condition, _ => null }).Where(c => c is not null))
            {
                var text = Regex.Replace(cond!.ToString(), @"\s+", " ").Trim();
                if (text.Length < 30 || !(text.Contains("&&") || text.Contains("||"))) continue;
                if (!predicates.TryGetValue(text, out var l)) predicates[text] = l = new();
                l.Add((rel, Cs.Line(cond)));
            }
        }
        // which side of a clone is the copy? the file that entered the history later; files born together are reported on both sides
        var history = ctx.History;
        DateTimeOffset? Born(string file) => history.Available ? history.Touching(file).Select(c => (DateTimeOffset?)c.Date).Min() : null;
        bool IsOriginal(string file, IEnumerable<string> others) { var mine = Born(file); return mine is not null && others.Select(Born).Any(o => o is not null && o > mine); }
        // X10: identical non-trivial predicates in two or more files
        foreach (var (text, sites) in predicates.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (sites.Select(s => s.file).Distinct().Count() < 2) continue;
            foreach (var s in sites) ctx.Add(new Finding("duplicated-code", "X10", $"Predicate `{(text.Length > 90 ? text[..90] + "…" : text)}` is written out identically in {sites.Select(x => x.file).Distinct().Count()} files", s.file, s.line, s.line, 0.5));
        }


        // D4: token-window clones (identifiers and literals kept: copies that still agree on their names and constants)
        var index = new Dictionary<string, List<(int s, int i)>>(StringComparer.Ordinal);
        for (var s = 0; s < streams.Count; s++)
        {
            var toks = streams[s];
            for (var i = 0; i + Window <= toks.Count; i++)
            {
                var key = string.Join('\u0001', toks.Skip(i).Take(Window).Select(t => t.Text));
                if (!index.TryGetValue(key, out var l)) index[key] = l = new();
                l.Add((s, i));
            }
        }
        var covered = streams.Select(st => new bool[st.Count]).ToList();
        var clones = new List<(Tok a0, Tok a1, Tok b0, Tok b1, int tokens)>();
        foreach (var (key, sites) in index.Where(k => k.Value.Count > 1).OrderBy(k => k.Value[0].s).ThenBy(k => k.Value[0].i))
        {
            for (var x = 0; x < sites.Count; x++) for (var y = x + 1; y < sites.Count; y++)
            {
                var (sa, ia) = sites[x]; var (sb, ib) = sites[y];
                if (sa == sb && Math.Abs(ia - ib) < Window) continue;
                if (covered[sa][ia] && covered[sb][ib]) continue;
                var len = Window;
                while (ia + len < streams[sa].Count && ib + len < streams[sb].Count && streams[sa][ia + len].Text == streams[sb][ib + len].Text && !(sa == sb && ib < ia + len + 1 && ia < ib + len + 1)) len++;
                if (len < MinClone) continue;
                var a0 = streams[sa][ia]; var a1 = streams[sa][ia + len - 1]; var b0 = streams[sb][ib]; var b1 = streams[sb][ib + len - 1];
                if (a1.Line - a0.Line + 1 < MinLines || b1.Line - b0.Line + 1 < MinLines) continue;
                for (var k = 0; k < len; k++) { covered[sa][ia + k] = true; covered[sb][ib + k] = true; }
                clones.Add((a0, a1, b0, b1, len));
            }
        }
        // one finding per distinct region (merge overlaps per file)
        var regions = clones.SelectMany(c => new[] { (file: c.a0.File, start: c.a0.Line, end: c.a1.Line, other: $"{c.b0.File}:{c.b0.Line}", c.tokens), (file: c.b0.File, start: c.b0.Line, end: c.b1.Line, other: $"{c.a0.File}:{c.a0.Line}", c.tokens) })
            .GroupBy(r => r.file, StringComparer.Ordinal).SelectMany(g =>
            {
                var merged = new List<(string file, int start, int end, string other, int tokens)>();
                foreach (var r in g.OrderBy(r => r.start))
                {
                    if (merged.Count > 0 && r.start <= merged[^1].end + 3) { var m = merged[^1]; merged[^1] = (m.file, m.start, Math.Max(m.end, r.end), m.other, m.tokens + r.tokens); }
                    else merged.Add(r);
                }
                return merged;
            }).OrderBy(r => r.file, StringComparer.Ordinal).ThenBy(r => r.start).ToList();
        var reported = regions.Where(r => !IsOriginal(r.file, new[] { r.other.Split(':')[0] })).ToList();
        foreach (var r in reported) ctx.Add(new Finding("duplicated-code", "D4", $"Lines {r.start}–{r.end} duplicate {r.tokens}+ tokens also found at {r.other}", r.file, r.start, r.end, Math.Min(2, r.tokens / 100.0 + 0.5)));
        ctx.Measure("X10", Shape.FromFindings(ctx.FindingsFor("X10"), ctx.ProductionKloc, 0.5), note: $"{ctx.FindingsFor("X10").Count()} duplicated predicate sites");
        var totalTokens = streams.Sum(s => s.Count);
        var dupTokens = covered.Sum(c => c.Count(x => x));
        var share = totalTokens == 0 ? 0 : (double)dupTokens / totalTokens;
        ctx.Measure("D4", 10 * Math.Max(0, 1 - share * 5), note: $"{dupTokens} of {totalTokens} tokens ({share:P1}) sit in {regions.Count} cloned regions (≥{MinClone} tokens, ≥{MinLines} lines)");
    }

    // ---------- D17 / GD1 / IC1 ----------
    private static readonly Regex Marker = new(@"\b(TODO|FIXME|HACK|XXX|TEMPORARY|KLUDGE)\b", RegexOptions.Compiled);
    private static readonly Regex CodeLike = new(@"(;\s*$|\{\s*$|^\s*\}\s*$|^\s*(if|for|foreach|while|return|var|using|public|private|protected|internal|switch|case|try|catch|else|throw|new)\b.*[(=;])", RegexOptions.Compiled);
    private static readonly string[] KnownSymbols = { "DEBUG", "TRACE", "RELEASE", "NET", "NETSTANDARD", "NETCOREAPP", "NETFRAMEWORK", "WINDOWS", "LINUX", "OSX", "MACOS", "ANDROID", "IOS", "BROWSER", "UNITY", "SILVERLIGHT", "WPF", "WINUI", "MAUI", "TIZEN", "FREEBSD", "NET_" };

    private static void Debt(ScanContext ctx)
    {
        var defined = ctx.Repo.Projects.SelectMany(p => p.DefineConstants).ToHashSet(StringComparer.Ordinal);
        var obsoleteUses = new Dictionary<string, (string file, int line, string name, List<string> callers)>(StringComparer.Ordinal);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            var root = tree.GetRoot();
            var model = p.Model(tree);
            var text = tree.GetText();
            // markers and commented-out code live in trivia only (a "TODO" string literal is data)
            var comments = root.DescendantTrivia(descendIntoTrivia: true).Where(Cs.IsComment).ToList();
            foreach (var c in comments)
            {
                foreach (var l in Cs.CommentLines(c))
                {
                    var m = Marker.Match(l);
                    if (m.Success) { ctx.Add(new Finding("technical-debt-marker", "D17", $"{m.Value} marker: {Trunc(l, 100)}", rel, Cs.Line(c.Token) + LineOffset(c, l), null, 0.5)); break; }
                }
            }
            var singles = comments.Where(c => c.IsKind(SyntaxKind.SingleLineCommentTrivia)).Select(c => (line: text.Lines.GetLineFromPosition(c.SpanStart).LineNumber + 1, body: c.ToString().TrimStart('/').Trim())).OrderBy(c => c.line).ToList();
            for (var i = 0; i < singles.Count;)
            {
                var j = i; var codeLines = 0;
                while (j < singles.Count && (j == i || singles[j].line == singles[j - 1].line + 1)) { if (CodeLike.IsMatch(singles[j].body) && !Marker.IsMatch(singles[j].body)) codeLines++; j++; }
                var runLen = j - i;
                var single = runLen == 1 && codeLines == 1 && singles[i].body.EndsWith(';') && singles[i].body.Contains('(') && singles[i].body.Contains(')');
                if (codeLines >= 2 && codeLines * 2 >= runLen || single)
                    ctx.Add(new Finding("commented-out-code", "D17", $"{codeLines} commented-out line(s) of code starting `{Trunc(singles[i].body, 60)}`", rel, singles[i].line, singles[j - 1].line, 0.5));
                i = j;
            }
            // suppressions
            foreach (var pragma in root.DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().Where(d => d.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
            {
                var codes = pragma.ErrorCodes.Select(e => e.ToString()).ToList();
                var line = Cs.Line(pragma);
                var restored = root.DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().Any(d => d.DisableOrRestoreKeyword.IsKind(SyntaxKind.RestoreKeyword) && Cs.Line(d) > line && (codes.Count == 0 || d.ErrorCodes.Select(e => e.ToString()).Intersect(codes).Any() || d.ErrorCodes.Count == 0));
                var justified = Enumerable.Range(Math.Max(1, line - 2), 3).Any(l => text.Lines[l - 1].ToString().Contains("//")) ;
                if (!restored || !justified)
                    ctx.Add(new Finding("suppressed-diagnostic", "D17", $"#pragma warning disable {string.Join(", ", codes)}{(codes.Count == 0 ? "(all)" : "")} {(restored ? "" : "never restored")}{(justified ? "" : (restored ? "" : ", ") + "no reason given")}", rel, line, line, codes.Count == 0 ? 2 : 1));
            }
            foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>().Where(a => Cs.Simple(a.Name) is "SuppressMessage" or "SuppressMessageAttribute"))
                if (!(attr.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.Text == "Justification" && a.Expression is LiteralExpressionSyntax { Token.ValueText.Length: > 3 }) ?? false))
                    ctx.Add(new Finding("suppressed-diagnostic", "D17", $"[SuppressMessage] without a justification", rel, Cs.Line(attr), null, 1));
            // unreachable: #if on a symbol nothing defines, constant conditions, case labels a normalised subject can never equal, else-if repeating an earlier arm
            foreach (var dir in root.DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<IfDirectiveTriviaSyntax>())
            {
                foreach (var id in dir.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    var sym = id.Identifier.Text;
                    if (defined.Contains(sym) || KnownSymbols.Any(k => sym.StartsWith(k, StringComparison.Ordinal))) continue;
                    if (id.Parent is PrefixUnaryExpressionSyntax) continue;
                    ctx.Add(new Finding("unreachable-code", "D17", $"#if {sym}: no project defines this symbol, so the region is dead by construction", rel, Cs.Line(dir), null, 1));
                    break;
                }
            }
            foreach (var ifs in root.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifs.Condition is LiteralExpressionSyntax lit && (lit.IsKind(SyntaxKind.FalseLiteralExpression) || lit.IsKind(SyntaxKind.TrueLiteralExpression)))
                    ctx.Add(new Finding("unreachable-code", "D17", $"if ({lit}) makes a branch unreachable", rel, Cs.Line(ifs), null, 1));
                if (ifs.Else?.Statement is IfStatementSyntax elseIf && Norm(elseIf.Condition) == Norm(ifs.Condition))
                    ctx.Add(new Finding("unreachable-code", "D17", $"else-if repeats the condition of the arm above it, so it can never be taken", rel, Cs.Line(elseIf), null, 1));
            }
            foreach (var sw in root.DescendantNodes().OfType<SwitchStatementSyntax>())
            {
                var subj = sw.Expression.ToString();
                var upper = subj.Contains(".ToUpper", StringComparison.Ordinal); var lower = subj.Contains(".ToLower", StringComparison.Ordinal);
                if (!upper && !lower) continue;
                foreach (var label in sw.Sections.SelectMany(s => s.Labels).OfType<CaseSwitchLabelSyntax>())
                    if (label.Value is LiteralExpressionSyntax { Token.Value: string s } && (upper && s.Any(char.IsLower) || lower && s.Any(char.IsUpper)))
                        ctx.Add(new Finding("unreachable-code", "D17", $"case \"{s}\" can never match a subject normalised with {(upper ? "ToUpper" : "ToLower")}", rel, Cs.Line(label), null, 1));
            }
            // not-implemented stubs and shape-level incompleteness
            foreach (var m in Cs.MethodLike(root))
            {
                var body = Cs.BodyOf(m); if (body is null) continue;
                if (OnlyThrows(body, "NotImplementedException"))
                {
                    var loc = ctx.Loc(Cs.IdentifierOf(m));
                    ctx.Add(new Finding("not-implemented-placeholder", "GD1", $"{Cs.TypeName(m)}.{Cs.NameOf(m)} only throws NotImplementedException: a stub shipped as production code", loc.File, loc.Line, loc.Line, 1));
                }
                if (m is MethodDeclarationSyntax md && md.ParameterList.Parameters.Count > 0 && !md.Modifiers.Any(SyntaxKind.AbstractKeyword) && !md.Modifiers.Any(SyntaxKind.OverrideKeyword) && md.ExplicitInterfaceSpecifier is null)
                {
                    var typeName = Cs.TypeName(md);
                    if (Regex.IsMatch(typeName, "Null|Fake|Stub|Mock|Noop|NoOp|Dummy|Empty")) continue;
                    var ret = body is BlockSyntax b && b.Statements.Count == 1 ? (b.Statements[0] as ReturnStatementSyntax)?.Expression : body is ArrowExpressionClauseSyntax arrow ? arrow.Expression : null;
                    if (ret is LiteralExpressionSyntax or DefaultExpressionSyntax || ret is MemberAccessExpressionSyntax ma && ma.ToString().EndsWith(".Empty"))
                    {
                        var used = body.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).ToHashSet(StringComparer.Ordinal);
                        if (!md.ParameterList.Parameters.Any(pp => used.Contains(pp.Identifier.Text)) && !Cs.HasAttribute(md.AttributeLists, "Obsolete"))
                        {
                            var loc = ctx.Loc(md.Identifier);
                            ctx.Add(new Finding("incomplete-implementation", "IC1", $"{typeName}.{md.Identifier.Text} takes {md.ParameterList.Parameters.Count} input(s), reads none of them and returns the constant `{ret}`", loc.File, loc.Line, loc.Line, 1));
                        }
                    }
                }
            }
            foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is not InterfaceDeclarationSyntax))
            {
                var members = t.Members.Where(mm => mm is MethodDeclarationSyntax or PropertyDeclarationSyntax).ToList();
                if (members.Count >= 3 && members.Count(mm => mm.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax) && mm.ToString().Contains("NotImplementedException")) * 2 > members.Count)
                { var loc = ctx.Loc(t); ctx.Add(new Finding("incomplete-implementation", "IC1", $"{t.Identifier.Text} is a skeleton: most of its members are not-implemented holes", loc.File, loc.Line, loc.Line, 1)); }
            }
            // obsolete symbols still used: reported once, at the [Obsolete] declaration, naming the callers
            foreach (var node in root.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax))
            {
                if (node is IdentifierNameSyntax && node.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax) continue;
                if (node is MemberAccessExpressionSyntax && node.Parent is InvocationExpressionSyntax) continue;
                var sym = Cs.SymbolOf(model, node is InvocationExpressionSyntax inv ? inv.Expression : node is ObjectCreationExpressionSyntax oc ? oc : node);
                if (sym is null || sym.Locations.All(l => l.IsInMetadata)) continue;
                var target = sym is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor ? (ISymbol)ctor.ContainingType : sym;
                if (!IsObsolete(target) && !(sym is IMethodSymbol ms && IsObsolete(ms))) continue;
                var enclosing = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                if (enclosing is not null && (Cs.HasAttribute(enclosing.AttributeLists, "Obsolete") || enclosing.Ancestors().OfType<TypeDeclarationSyntax>().Any(td => Cs.HasAttribute(td.AttributeLists, "Obsolete")))) continue;
                var declLoc = target.Locations.FirstOrDefault(l => l.IsInSource);
                if (declLoc is null) continue;
                var declFile = ctx.Workspace.RelPath(declLoc.SourceTree!.FilePath); var declLine = declLoc.GetLineSpan().StartLinePosition.Line + 1;
                var key = $"{declFile}:{declLine}:{target.Name}";
                if (!obsoleteUses.TryGetValue(key, out var uses)) obsoleteUses[key] = uses = (declFile, declLine, target.Name, new List<string>());
                uses.callers.Add($"{Path.GetFileName(rel)}:{Cs.Line(node)}");
            }
            // unused private members (project-wide name search; written-but-never-read fields included, lifetime-keeping fields excluded)
            foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (t.Modifiers.Any(SyntaxKind.PartialKeyword)) continue;
                foreach (var member in t.Members)
                {
                    IEnumerable<(SyntaxToken id, string kind, string? type)> decls = member switch
                    {
                        MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.PrivateKeyword) && md.AttributeLists.Count == 0 && md.ExplicitInterfaceSpecifier is null => new[] { (md.Identifier, "method", (string?)null) },
                        FieldDeclarationSyntax fd when fd.Modifiers.Any(SyntaxKind.PrivateKeyword) && !fd.Modifiers.Any(SyntaxKind.ConstKeyword) && fd.AttributeLists.Count == 0 => fd.Declaration.Variables.Select(v => (v.Identifier, "field", (string?)Cs.Simple(fd.Declaration.Type))),
                        PropertyDeclarationSyntax pd when pd.Modifiers.Any(SyntaxKind.PrivateKeyword) && pd.AttributeLists.Count == 0 => new[] { (pd.Identifier, "property", (string?)null) },
                        _ => Array.Empty<(SyntaxToken, string, string?)>(),
                    };
                    foreach (var (id, kind, type) in decls)
                    {
                        if (id.Text is "Dispose" or "Finalize" || id.Text.StartsWith("On")) continue;
                        var refs = p.Trees.SelectMany(tr => tr.GetRoot().DescendantTokens().Where(tok => tok.IsKind(SyntaxKind.IdentifierToken) && tok.Text == id.Text && tok != id)).ToList();
                        var reads = refs.Where(r => !(r.Parent?.Parent is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.SimpleAssignmentExpression) && asg.Left.Span.Contains(r.Span)) && !(r.Parent is VariableDeclaratorSyntax)).ToList();
                        if (refs.Count > 0 && (kind != "field" || reads.Count > 0)) continue;
                        if (kind == "field" && refs.Count > 0 && reads.Count == 0)
                        {
                            // written, never read: a field that only keeps something alive (a Timer, a watcher, a registration) is doing its job
                            var keepsAlive = type is not null && Regex.IsMatch(type, "Timer|Watcher|Registration|Subscription|Disposable|Handle|Listener|Hosted|Thread|Task|Lock|Semaphore|Mutex|Channel|Observer")
                                || refs.Any(r => r.Parent?.Parent is AssignmentExpressionSyntax a && a.Right is ObjectCreationExpressionSyntax oc && Regex.IsMatch(Cs.Simple(oc.Type), "Timer|Watcher|Registration|Subscription|Listener|Thread"));
                            if (keepsAlive) continue;
                            ctx.Add(new Finding("unused-code", "D17", $"private field {id.Text} is written but never read", rel, Cs.Line(id), null, 1));
                            continue;
                        }
                        ctx.Add(new Finding("unused-code", "D17", $"private {kind} {id.Text} is never referenced", rel, Cs.Line(id), null, 1));
                    }
                }
            }
        }
        foreach (var (file, line, name, callers) in obsoleteUses.Values.OrderBy(u => u.file, StringComparer.Ordinal).ThenBy(u => u.line))
            ctx.Add(new Finding("obsolete-symbol-still-used", "D17", $"{name} is marked [Obsolete] by this codebase and still used {callers.Count} time(s): {string.Join(", ", callers.Take(5))}", file, line, line, Math.Min(2, 0.5 + callers.Count * 0.5)));
        // project-level suppressions: reported once, at the file that declares them, and only when no comment beside them says why
        var declaring = ctx.Repo.Files.Where(f => f.EndsWith(".csproj") || f.EndsWith(".props")).Where(f => ctx.Repo.Projects.Any(p => p.IsProduction && (p.Path == f || f.EndsWith("Directory.Build.props"))));
        foreach (var f in declaring.Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            var text = ctx.Repo.Text(f); var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"<(NoWarn|WarningsNotAsErrors)>([^<]*)</");
                if (!m.Success) continue;
                var specific = m.Groups[2].Value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => Regex.IsMatch(c, @"^(CS|CA|IDE|SA|S|NU|MA|RS|xUnit|NUnit)\w*\d+$", RegexOptions.IgnoreCase) && c is not ("CS1591" or "NU1603" or "NU1605" or "NU1701")).ToList();
                if (specific.Count == 0) continue;
                var justified = Enumerable.Range(Math.Max(0, i - 3), 3).Any(j => lines[j].Contains("<!--")) || lines[i].Contains("<!--");
                if (justified) continue;
                ctx.Add(new Finding("suppressed-diagnostic", "D17", $"<{m.Groups[1].Value}> demotes {string.Join(", ", specific)} build-wide, with no reason recorded", f, i + 1, i + 1, 1));
            }
        }
        var d17 = ctx.FindingsFor("D17").ToList();
        ctx.Measure("D17", Shape.FromFindings(d17, ctx.ProductionKloc, 0.7), note: $"{d17.Count} explicit-debt findings: {string.Join(", ", d17.GroupBy(f => f.RuleId).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))}");
        ctx.Measure("GD1", Shape.FromFindings(ctx.FindingsFor("GD1"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("GD1").Count()} not-implemented stubs");
        ctx.Measure("IC1", Shape.FromFindings(ctx.FindingsFor("IC1").Concat(ctx.FindingsFor("GD1")).Concat(d17.Where(f => f.RuleId == "unreachable-code")), ctx.ProductionKloc), note: "stubs, constant-returning members, skeleton types and dead branches");
    }

    private static bool IsObsolete(ISymbol s) => s.GetAttributes().Any(a => a.AttributeClass?.Name is "ObsoleteAttribute" or "Obsolete");
    private static string Norm(SyntaxNode n) => Regex.Replace(n.ToString(), @"\s+", "");
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static int LineOffset(SyntaxTrivia c, string line) { var idx = 0; foreach (var l in Cs.CommentLines(c)) { if (l == line) return idx; idx++; } return 0; }
    private static int? LineOf(string text, string needle) { var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); return i < 0 ? null : text.Take(i).Count(ch => ch == '\n') + 1; }
    private static bool OnlyThrows(SyntaxNode body, string exception) => body switch
    {
        BlockSyntax b => b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax ts && ts.Expression is ObjectCreationExpressionSyntax oc && Cs.Simple(oc.Type) == exception,
        ArrowExpressionClauseSyntax a => a.Expression is ThrowExpressionSyntax te && te.Expression is ObjectCreationExpressionSyntax oc2 && Cs.Simple(oc2.Type) == exception,
        _ => false,
    };

    // ---------- X1 / X2 / PF3 ----------
    private static readonly Regex IoReceiver = new("Http|Stream|Reader|Writer|Db|Context|Client|Connection|Command|Socket|File|Channel|Bus|Queue|Repository|Store|Cache", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void Async(ScanContext ctx)
    {
        int asyncMethods = 0, withToken = 0, awaits = 0, awaitsWithoutCa = 0; var packableAwaits = false;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot(); var model = p.Model(tree);
            var isLibrary = p.Project.IsPackable && p.Project.Role == ProjectRole.Library && !string.Equals(p.Project.OutputType, "Exe", StringComparison.OrdinalIgnoreCase);   // a packable web host or worker is an application, whatever its PackageId says
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax ma when ma.Name.Identifier.Text == "Result" && !(ma.Parent is InvocationExpressionSyntax) && IsTask(model, ma.Expression):
                        Blocking(ctx, rel, ma, $"`.Result` blocks on {Trunc(ma.Expression.ToString(), 60)}"); break;
                    case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax wm && wm.Name.Identifier.Text == "Wait" && inv.ArgumentList.Arguments.Count <= 1 && IsTask(model, wm.Expression):
                        Blocking(ctx, rel, inv, $"`.Wait()` blocks on {Trunc(wm.Expression.ToString(), 60)}"); break;
                    case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax gr && gr.Name.Identifier.Text == "GetResult" && gr.Expression is InvocationExpressionSyntax ga && Cs.MemberName(ga.Expression) == "GetAwaiter":
                        Blocking(ctx, rel, inv, $"`.GetAwaiter().GetResult()` blocks on {Trunc(Cs.ReceiverText(ga.Expression), 60)}"); break;
                    case MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.AsyncKeyword):
                        var retName = Cs.Simple(md.ReturnType);
                        if (retName == "void")
                        {
                            if (!Cs.IsEventHandlerSignature(md.ParameterList) && !Cs.HasAttribute(md.AttributeLists, "RelayCommand", "Fact", "Test", "TestMethod"))
                            { var loc = ctx.Loc(md.Identifier); ctx.Add(new Finding("async-void-method", "X1", $"{Cs.TypeName(md)}.{md.Identifier.Text} is async void outside an event handler: exceptions escape to the synchronisation context and callers cannot await it", loc.File, loc.Line, loc.Line, 1)); }
                        }
                        else if (retName is "Task" or "ValueTask" && !Cs.IsEventHandlerSignature(md.ParameterList) && !Cs.HasAttribute(md.AttributeLists, "Fact", "Theory", "Test", "TestMethod") && md.Body is not null)
                        {
                            var awaitNodes = md.Body.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<AwaitExpressionSyntax>().ToList();
                            if (awaitNodes.Count == 0) break;
                            asyncMethods++;
                            var tokenParam = md.ParameterList.Parameters.FirstOrDefault(pp => Cs.Simple(pp.Type) == "CancellationToken");
                            var hasToken = tokenParam is not null || md.Modifiers.Any(SyntaxKind.OverrideKeyword) || md.ExplicitInterfaceSpecifier is not null;
                            if (tokenParam is not null)
                            {
                                // accepted but not forwarded: an awaited I/O call with no token argument while one is in hand
                                var tokenName = tokenParam.Identifier.Text;
                                var dropped = awaitNodes.Select(a => a.Expression is InvocationExpressionSyntax ca && Cs.MemberName(ca.Expression) == "ConfigureAwait" ? (ca.Expression as MemberAccessExpressionSyntax)?.Expression : a.Expression).OfType<InvocationExpressionSyntax>()
                                    .FirstOrDefault(i => Cs.MemberName(i.Expression).EndsWith("Async", StringComparison.Ordinal) && (IoReceiver.IsMatch(ReceiverTypeName(model, i)) || QueryTerminal.IsMatch(Cs.MemberName(i.Expression))) && !i.ArgumentList.Arguments.Any(ar => ar.ToString().Contains(tokenName) || ar.ToString().EndsWith("Token") || ar.ToString().Contains("CancellationToken")) && (AcceptsToken(model, i) || QueryTerminal.IsMatch(Cs.MemberName(i.Expression))));
                                if (dropped is not null) { var loc = ctx.Loc(md.Identifier); ctx.Add(new Finding("missing-cancellation-propagation", "X2", $"{Cs.TypeName(md)}.{md.Identifier.Text} accepts {tokenName} but does not pass it to {Trunc(dropped.Expression.ToString(), 50)}: an aborted caller keeps the work running", loc.File, loc.Line, loc.Line, 1)); }
                                else if (md.Identifier.Text == "ExecuteAsync" && !md.Body.ToString().Contains(tokenName) ) { var loc = ctx.Loc(md.Identifier); ctx.Add(new Finding("missing-cancellation-propagation", "X2", $"{Cs.TypeName(md)}.ExecuteAsync never reads its {tokenName}: the host cannot stop it gracefully", loc.File, loc.Line, loc.Line, 1)); }
                            }
                            if (hasToken) withToken++;
                            else
                            {
                                var io = awaitNodes.Select(a => a.Expression is InvocationExpressionSyntax ca && Cs.MemberName(ca.Expression) == "ConfigureAwait" ? (ca.Expression as MemberAccessExpressionSyntax)?.Expression : a.Expression).OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Cs.MemberName(i.Expression).EndsWith("Async", StringComparison.Ordinal) && (IoReceiver.IsMatch(ReceiverTypeName(model, i)) || QueryTerminal.IsMatch(Cs.MemberName(i.Expression))) && !i.ArgumentList.Arguments.Any(ar => ar.ToString().Contains("CancellationToken") || ar.ToString().EndsWith("Token")));
                                if (io is not null) { var loc = ctx.Loc(md.Identifier); ctx.Add(new Finding("missing-cancellation-propagation", "X2", $"{Cs.TypeName(md)}.{md.Identifier.Text} awaits {Trunc(io.Expression.ToString(), 50)} with no CancellationToken to pass on", loc.File, loc.Line, loc.Line, 1)); }
                            }
                        }
                        break;
                    case AwaitExpressionSyntax aw when isLibrary && !Cs.IsTopLevel(aw):
                        awaits++; packableAwaits = true;
                        var confAwait = aw.Expression is InvocationExpressionSyntax ci && Cs.MemberName(ci.Expression) == "ConfigureAwait";
                        if (!confAwait) { awaitsWithoutCa++; ctx.Add(new Finding("missing-configure-await", "PF3", $"await in packable library {p.Project.Name} without ConfigureAwait(false): {Trunc(aw.Expression.ToString(), 60)}", rel, Cs.Line(aw), null, 0.25)); }
                        break;
                }
            }
        }
        ctx.Measure("X1", Shape.FromFindings(ctx.FindingsFor("X1"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("X1").Count()} sync-over-async / async-void sites");
        if (asyncMethods == 0) ctx.Skip("X2", "no async methods with awaits in production code");
        else ctx.Measure("X2", Shape.FromShare((double)withToken / asyncMethods), note: $"{withToken} of {asyncMethods} awaiting async methods accept a CancellationToken");
        var pf3 = ctx.FindingsFor("X1").Where(f => f.RuleId == "blocking-on-async-code").Concat(ctx.FindingsFor("PF3")).ToList();
        if (pf3.Count == 0 && !ctx.Workspace.ProductionTrees.Any(t => t.tree.GetRoot().DescendantNodes().OfType<AwaitExpressionSyntax>().Any())) ctx.Skip("PF3", "no asynchronous code in production projects");
        else ctx.Measure("PF3", Shape.FromFindings(pf3, ctx.ProductionKloc), note: packableAwaits ? $"{awaitsWithoutCa} of {awaits} library awaits lack ConfigureAwait(false); {pf3.Count(f => f.RuleId == "blocking-on-async-code")} blocking sites" : $"{pf3.Count} blocking sites; no packable library, so ConfigureAwait is not required");
    }

    private static void Blocking(ScanContext ctx, string rel, SyntaxNode node, string what)
    {
        var method = Cs.EnclosingMethod(node);
        if (Cs.IsMain(method) || Cs.IsTopLevel(node)) return;                     // a console Main has no context to deadlock
        if (method is not null && Cs.TypeName(method) == "Program") return;
        ctx.Add(new Finding("blocking-on-async-code", "X1", $"{what}: sync-over-async in {Cs.TypeName(node)}.{(method is null ? "?" : Cs.NameOf(method))} risks deadlock and starves the thread pool", rel, Cs.Line(node), null, 1));
    }

    private static bool IsTask(SemanticModel model, ExpressionSyntax e)
    {
        var t = Cs.TypeOf(model, e);
        if (t is not null && t.TypeKind != TypeKind.Error) return Cs.IsTaskLike(t);
        return Cs.LooksAsyncCall(e) || e.ToString().Contains("Task") ;
    }

    /// <summary>Does the called method (or an overload of it) take a CancellationToken? Unresolved methods are assumed to.</summary>
    private static bool AcceptsToken(SemanticModel model, InvocationExpressionSyntax inv)
    {
        if (Cs.SymbolOf(model, inv.Expression) is not IMethodSymbol m || m.ContainingType is null || m.ContainingType.TypeKind == TypeKind.Error) return !Regex.IsMatch(inv.Expression.ToString(), @"\b(output|writer|console|Console|stdout|textWriter)\b", RegexOptions.IgnoreCase);
        var target = m.ReducedFrom ?? m.OriginalDefinition;
        var candidates = target.ContainingType.GetMembers(target.Name).OfType<IMethodSymbol>();
        // the SAME call with a trailing CancellationToken must exist: every other parameter identical in type
        return candidates.Any(o => o.Parameters.Length == target.Parameters.Length + 1 && o.Parameters[^1].Type.Name == "CancellationToken"
            && o.Parameters.Take(target.Parameters.Length).Select(x => x.Type.ToDisplayString()).SequenceEqual(target.Parameters.Select(x => x.Type.ToDisplayString())));
    }

    private static string ReceiverTypeName(SemanticModel model, InvocationExpressionSyntax inv)
    {
        if (inv.Expression is MemberAccessExpressionSyntax ma)
        {
            var t = Cs.TypeOf(model, ma.Expression);
            if (t is not null && t.TypeKind != TypeKind.Error) return t.Name;
            return ma.Expression.ToString();
        }
        return "";
    }

    // ---------- X3 ----------
    private static void Exceptions(ScanContext ctx)
    {
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var c in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var stmts = c.Block.Statements;
                var typeName = Cs.Simple(c.Declaration?.Type);
                var broad = c.Declaration is null || typeName is "Exception" or "SystemException" or "AggregateException";
                var hasComment = c.Block.DescendantTrivia().Any(Cs.IsComment);
                var cancellation = typeName is "OperationCanceledException" or "TaskCanceledException";   // swallowing a cancellation on shutdown is the idiom
                if (stmts.Count == 0 && !cancellation && (broad || !hasComment))
                    ctx.Add(new Finding("empty-catch-block", "X3", $"empty catch{(c.Declaration is null ? "" : $" ({typeName})")} swallows the exception{(broad ? "" : " without saying why")}", rel, Cs.Line(c), null, broad ? 1 : 0.5));
                if (stmts.Count == 1 && stmts[0] is ThrowStatementSyntax t0 && t0.Expression is null && c.Filter is null)
                    ctx.Add(new Finding("pointless-catch-rethrow", "X3", $"catch{(c.Declaration is null ? "" : $" ({typeName})")} only rethrows: the clause does nothing", rel, Cs.Line(c), null, 0.5));
                var variable = c.Declaration?.Identifier.Text;
                if (variable is { Length: > 0 })
                    foreach (var th in c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Where(th => th.Expression is IdentifierNameSyntax id && id.Identifier.Text == variable))
                        ctx.Add(new Finding("rethrow-resets-stack-trace", "X3", $"`throw {variable};` discards the original stack trace; use `throw;`", rel, Cs.Line(th), null, 1));
            }
        }
        ctx.Measure("X3", Shape.FromFindings(ctx.FindingsFor("X3"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("X3").Count()} swallowed or trace-losing exception sites");
    }

    // ---------- X4 / X23 ----------
    private static readonly HashSet<string> LogMethods = new(StringComparer.Ordinal) { "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
    private static readonly Regex Expensive = new(@"string\.Join|\.Select\(|JsonSerializer\.Serialize|\.ToList\(\)|\.ToArray\(\)|string\.Concat|\.Aggregate\(|SerializeObject|\.Dump\(", RegexOptions.Compiled);

    private static void Logging(ScanContext ctx)
    {
        int logCalls = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var model = p.Model(tree);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Cs.MemberName(inv.Expression);
                if (!LogMethods.Contains(name)) continue;
                var receiver = Cs.ReceiverText(inv.Expression);
                if (!receiver.Contains("log", StringComparison.OrdinalIgnoreCase) && !receiver.EndsWith("Log")) continue;
                logCalls++;
                foreach (var arg in inv.ArgumentList.Arguments)
                {
                    if (arg.Expression is InterpolatedStringExpressionSyntax interp)
                    {
                        var holes = interp.Contents.OfType<InterpolationSyntax>().ToList();
                        if (holes.Count == 0) continue;
                        bool allConst = holes.All(h => IsConst(model, h.Expression));
                        if (allConst) continue;
                        ctx.Add(new Finding("non-structured-log-message", "X4", $"{name}($\"…\") interpolates {holes.Count} runtime value(s) into the message instead of a message template", rel, Cs.Line(inv), null, 1));
                        break;
                    }
                    if (arg.Expression is InvocationExpressionSyntax fi && fi.Expression.ToString() is "string.Format" or "String.Format")
                    { ctx.Add(new Finding("non-structured-log-message", "X4", $"{name}(string.Format(…)) pre-formats the message instead of using a template", rel, Cs.Line(inv), null, 1)); break; }
                }
                if (name is "LogDebug" or "LogTrace" or "Debug" or "Verbose" && Expensive.IsMatch(inv.ArgumentList.ToString()))
                {
                    var guarded = inv.Ancestors().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString().Contains("IsEnabled"));
                    if (!guarded) ctx.Add(new Finding("unguarded-expensive-debug-logging", "X23", $"{name} builds {Trunc(Regex.Match(inv.ArgumentList.ToString(), Expensive.ToString()).Value, 40)} eagerly on every call, even when the level is off", rel, Cs.Line(inv), null, 1));
                }
            }
        }
        if (logCalls == 0) { ctx.Skip("X4", "no logger calls in production code"); ctx.Skip("X23", "no logger calls in production code"); return; }
        ctx.Measure("X4", Shape.FromShare(1 - (double)ctx.FindingsFor("X4").Count() / logCalls), note: $"{ctx.FindingsFor("X4").Count()} of {logCalls} log calls interpolate runtime values");
        ctx.Measure("X23", Shape.FromFindings(ctx.FindingsFor("X23"), ctx.ProductionKloc), note: $"{ctx.FindingsFor("X23").Count()} unguarded expensive debug/trace log calls");
    }

    // ---------- X5 ----------
    private static bool IsConst(SemanticModel model, ExpressionSyntax e)
    {
        if (e is InvocationExpressionSyntax inv && inv.Expression.ToString() == "nameof") return true;
        try { return model.GetConstantValue(e).HasValue; } catch (Exception) { return false; }
    }

    private static void Nullable(ScanContext ctx)
    {
        var prod = ctx.Repo.Projects.Where(p => p.IsProduction && p.SourceFiles.Count > 0).ToList();
        if (prod.Count == 0) { ctx.Skip("X5", "no production projects"); return; }
        var enabled = 0;
        foreach (var p in prod)
        {
            if (p.Nullable) { enabled++; continue; }
            ctx.Add(new Finding("nullable-analysis-disabled", "X5", $"{p.Name} ({p.Path}) does not enable nullable reference types while the analysis is a project-wide switch", null, null, null, 2));
        }
        int suppressions = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var bangs = tree.GetRoot().DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Where(u => u.IsKind(SyntaxKind.SuppressNullableWarningExpression)).ToList();
            suppressions += bangs.Count;
            if (bangs.Count >= 3)
            {
                var rel = ctx.Workspace.RelPath(tree.FilePath);
                ctx.Add(new Finding("null-forgiving-suppression", "X5", $"{bangs.Count} null-forgiving `!` operators in {Path.GetFileName(rel)}: the nullable analysis is being overridden rather than satisfied", rel, Cs.Line(bangs[0]), null, Math.Min(2, bangs.Count / 3.0)));
            }
        }
        var enabledShare = (double)enabled / prod.Count;
        var suppressionFactor = Shape.FromFindings(ctx.FindingsFor("X5").Where(f => f.RuleId == "null-forgiving-suppression"), ctx.ProductionKloc, 0.5) / 10;
        ctx.Measure("X5", Shape.FromShare(enabledShare) * suppressionFactor, note: $"{enabled} of {prod.Count} production projects enable nullable; {suppressions} `!` suppressions");
    }
}
