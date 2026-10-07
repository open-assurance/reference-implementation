using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Security &amp; Compliance: D29 (SAST), D30/D43/D44 (dependencies, platform), D37, S1, C1–C5, D32.</summary>
public static class Security
{
    public static void Run(ScanContext ctx)
    {
        Sast(ctx); Dependencies(ctx); Platform(ctx); Disclosure(ctx); WebPosture(ctx); Compliance(ctx);
    }

    // ---------------- D29: source weaknesses reachable from request input ----------------
    private static readonly Regex RequestSource = new(@"\b(Request\.(Query|Form|Headers|Cookies|RouteValues|Body|Path|QueryString)|HttpContext\.Request|FromQuery|FromRoute|FromBody|FromForm|FromHeader|context\.Request|req\.Query|Query\[|Form\[|Headers\[|RouteValues\[)", RegexOptions.Compiled);

    private static void Sast(ScanContext ctx)
    {
        var findings = new List<Finding>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot(); var model = p.Model(tree);
            var isWeb = p.Project.Role == ProjectRole.Web || p.Project.Sdk.Contains("Web") || root.ToString().Contains("Microsoft.AspNetCore");
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax inv: Invocation(ctx, findings, rel, model, inv, isWeb); break;
                    case ObjectCreationExpressionSyntax oc: Creation(ctx, findings, rel, model, oc, isWeb); break;
                    case AssignmentExpressionSyntax asg when asg.Left.ToString().EndsWith("CommandText") && BuiltFromInput(model, asg.Right, isWeb, (SyntaxNode?)(Cs.EnclosingMethod(asg) is { } em0 ? Cs.BodyOf(em0) : null) ?? asg):
                        findings.Add(new Finding("sql-injection", "D29", "CommandText assembled from request input: use parameters", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when asg.Left.ToString().EndsWith(".Filter") && BuiltFromInput(model, asg.Right, isWeb, (SyntaxNode?)(Cs.EnclosingMethod(asg) is { } em1 ? Cs.BodyOf(em1) : null) ?? asg) && Regex.IsMatch(asg.Right.ToString(), @"\(\w+=|objectClass|\(&|\(\|"):
                        findings.Add(new Finding("ldap-injection", "D29", "an LDAP filter is built from request input without RFC 4515 escaping", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when asg.Left.ToString().EndsWith("DtdProcessing") && asg.Right.ToString().EndsWith("Parse"):
                        findings.Add(new Finding("xml-external-entity", "D29", "DtdProcessing.Parse enables external entity expansion on an XML reader", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when asg.Left.ToString().EndsWith("XmlResolver") && asg.Right is ObjectCreationExpressionSyntax xr && Cs.Simple(xr.Type) is "XmlUrlResolver":
                        findings.Add(new Finding("xml-external-entity", "D29", "an XmlUrlResolver lets the parser fetch external entities", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when Regex.IsMatch(asg.Left.ToString(), @"TypeNameHandling$") && !asg.Right.ToString().EndsWith("None"):
                        findings.Add(new Finding("insecure-deserialization", "D29", $"TypeNameHandling.{asg.Right.ToString().Split('.').Last()} lets a JSON payload choose the type it deserialises to", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when asg.Left.ToString().EndsWith("ServerCertificateCustomValidationCallback") && Regex.IsMatch(asg.Right.ToString(), @"=>\s*true|DangerousAcceptAnyServerCertificateValidator"):
                        findings.Add(new Finding("improper-certificate-validation", "D29", "every server certificate is accepted: TLS without authentication", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when Regex.IsMatch(asg.Left.ToString(), @"Validate(Issuer|Audience|Lifetime|IssuerSigningKey)$") && asg.Right.IsKind(SyntaxKind.FalseLiteralExpression):
                        findings.Add(new Finding("token-signature-or-expiry-not-validated", "D29", $"{asg.Left.ToString().Split('.').Last()} = false: the token's {(asg.Left.ToString().Contains("Lifetime") ? "lifetime" : "signature/issuer")} is not checked", rel, Cs.Line(asg), null, 2)); break;
                    case AssignmentExpressionSyntax asg when Regex.IsMatch(asg.Left.ToString(), @"RequireHttpsMetadata$") && asg.Right.IsKind(SyntaxKind.FalseLiteralExpression):
                        findings.Add(new Finding("cleartext-transmission", "D29", "RequireHttpsMetadata = false: the identity provider's metadata may travel in clear text", rel, Cs.Line(asg), null, 1)); break;
                    case MemberAccessExpressionSyntax ma when Regex.IsMatch(ma.ToString(), @"^(MD5|SHA1|DES|TripleDES|RC2|Rijndael)\.Create$|^CipherMode\.ECB$|^HashAlgorithmName\.(MD5|SHA1)$|^new (MD5|SHA1)CryptoServiceProvider$"):
                        {
                            var alg = Regex.Match(ma.ToString(), @"MD5|SHA1|DES|TripleDES|RC2|Rijndael|ECB").Value;
                            var method = Cs.EnclosingMethod(ma); var body = method is null ? "" : Cs.BodyOf(method)?.ToString() ?? "";
                            var securityUse = Regex.IsMatch(body + Cs.TypeName(ma), @"(?i)password|secret|token|signature|sign|hmac|auth|credential|encrypt|key", RegexOptions.IgnoreCase);
                            var passwordUse = Regex.IsMatch(body + Cs.TypeName(ma), @"(?i)password|passwd|pwd");
                            if (alg is "MD5" or "SHA1" && passwordUse) findings.Add(new Finding("insufficient-password-hashing", "D29", $"{alg} over a password: no salt, no work factor; use PBKDF2, bcrypt, scrypt or Argon2", rel, Cs.Line(ma), null, 2));
                            else if (alg is "MD5" or "SHA1" && securityUse) findings.Add(new Finding("weak-hash-algorithm", "D29", $"{alg} used for a security purpose", rel, Cs.Line(ma), null, 1.5));
                            else if (alg is not ("MD5" or "SHA1")) findings.Add(new Finding("weak-cryptographic-algorithm", "D29", $"{alg} is a broken or risky algorithm/mode", rel, Cs.Line(ma), null, 2));
                            break;
                        }
                    case InterpolatedStringExpressionSyntax interp: Interpolation(ctx, findings, rel, model, interp, isWeb); break;
                }
            }
        }
        foreach (var f in findings.DistinctBy(f => (f.RuleId, f.File, f.Line))) ctx.Add(f);
        ctx.Measure("D29", Shape.FromFindings(ctx.FindingsFor("D29"), ctx.ProductionKloc, 1.5), note: $"{ctx.FindingsFor("D29").Count()} static-analysis finding(s): {string.Join(", ", ctx.FindingsFor("D29").GroupBy(f => f.RuleId).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))}");
    }

    /// <summary>Is this expression fed (in part) by request input, a method parameter of an endpoint, or a parameter of the enclosing public method?</summary>
    private static bool Tainted(SemanticModel model, ExpressionSyntax e, bool isWeb, bool strict = false)
    {
        var text = e.ToString();
        if (RequestSource.IsMatch(text)) return true;
        var method = Cs.EnclosingMethod(e);
        var ids = e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        if (method is null) return false;
        var parameters = method switch { BaseMethodDeclarationSyntax m => m.ParameterList.Parameters, LocalFunctionStatementSyntax l => l.ParameterList.Parameters, ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters, _ => default };
        var endpoint = method is MethodDeclarationSyntax md && (Cs.HasAttribute(md.AttributeLists, "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "Route") || Cs.TypeName(md).EndsWith("Controller") || Cs.TypeName(md).EndsWith("Endpoints") || Cs.TypeName(md).EndsWith("Handler") || Cs.TypeName(md).EndsWith("Service"))
            || method is LambdaExpressionSyntax && method.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => Regex.IsMatch(Cs.MemberName(i.Expression), @"^Map(Get|Post|Put|Delete|Patch|Methods)$"));
        var isPublic = method is MethodDeclarationSyntax mdd && mdd.Modifiers.Any(SyntaxKind.PublicKeyword);
        if (!(endpoint || isPublic && isWeb && !strict)) return false;
        foreach (var prm in parameters)
        {
            var t = Cs.Simple(prm.Type);
            if (t is "CancellationToken" or "ILogger" or "HttpContext" or "IServiceProvider") continue;
            if (ids.Contains(prm.Identifier.Text)) return true;
            // a property of a request/command object
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(prm.Identifier.Text)}\.\w+")) return true;
        }
        // locals assigned from request input or parameters inside the method
        var body = Cs.BodyOf(method);
        if (body is null) return false;
        foreach (var id in ids)
            foreach (var decl in body.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Identifier.Text == id && v.Initializer is not null))
                if (RequestSource.IsMatch(decl.Initializer!.Value.ToString()) || parameters.Any(pp => Regex.IsMatch(decl.Initializer.Value.ToString(), $@"\b{Regex.Escape(pp.Identifier.Text)}\b") && Cs.Simple(pp.Type) is not ("CancellationToken" or "ILogger"))) return true;
        return false;
    }

    /// <summary>A value that cannot carry an injection: numbers, GUIDs, booleans, dates, enums, or a lookup/switch over a fixed set.</summary>
    private static bool Harmless(SemanticModel model, ExpressionSyntax e, SyntaxNode scope)
    {
        var t = Cs.TypeOf(model, e);
        if (t is not null && t.TypeKind != TypeKind.Error && (t.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Boolean or SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_DateTime or SpecialType.System_Int16 or SpecialType.System_Byte || t.TypeKind == TypeKind.Enum || t.Name is "Guid" or "DateTimeOffset" or "DateOnly" or "TimeSpan")) return true;
        // a sanitiser or encoder applied right here
        if (e is InvocationExpressionSyntax callHere && Regex.IsMatch(Cs.MemberName(callHere.Expression), @"^(Escape\w*|Encode\w*|Saniti[sz]e\w*|Quote\w*|Normali[sz]e\w*|Verify\w*|Validate\w*|EscapeDataString|HtmlEncode|UrlEncode|ToString)$") && Cs.MemberName(callHere.Expression) != "ToString") return true;
        if (e is InvocationExpressionSyntax fmt && Cs.MemberName(fmt.Expression) == "ToString" && fmt.ArgumentList.Arguments.Count > 0) return true;   // a formatted number/guid
        if (e is IdentifierNameSyntax id)
        {
            // validated before use: a Verify/Validate/Ensure/Guard call (or a regex gate followed by a throw) on this very identifier
            if (scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Regex.IsMatch(Cs.MemberName(i.Expression), @"^(Verify\w*|Validate\w*|Ensure\w*|Guard\w*|Check\w*|ThrowIf\w*|RequireValid\w*)$") && i.ArgumentList.Arguments.Any(a => a.Expression.ToString() == id.Identifier.Text))) return true;
            if (scope.DescendantNodes().OfType<IfStatementSyntax>().Any(i => Regex.IsMatch(i.Condition.ToString(), $@"Regex\.IsMatch\(\s*{Regex.Escape(id.Identifier.Text)}\b|\b{Regex.Escape(id.Identifier.Text)}\.(All|Any)\(char\.|Contains\(\s*{Regex.Escape(id.Identifier.Text)}\s*\)") && i.Statement.DescendantNodesAndSelf().Any(n => n is ThrowStatementSyntax or ReturnStatementSyntax))) return true;
            var decl = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == id.Identifier.Text && v.Initializer is not null);
            var src = decl?.Initializer?.Value;
            if (src is SwitchExpressionSyntax) return true;
            if (src is ConditionalExpressionSyntax c && c.WhenTrue is LiteralExpressionSyntax && c.WhenFalse is LiteralExpressionSyntax) return true;
            if (src is InvocationExpressionSyntax inv && Regex.IsMatch(Cs.MemberName(inv.Expression), @"^(GetValueOrDefault|Clamp|Parse|TryParse|EscapeDataString|Escape\w*|Encode\w*|Saniti[sz]e\w*|Resolve\w*|Map\w*|Lookup\w*|Normali[sz]e\w*|Whitelist\w*|Allow\w*|Pick\w*|Choose\w*|Quote\w*)$")) return true;
            // a helper of the same type that decides from a fixed set (switch / dictionary / allow-list)
            if (src is InvocationExpressionSyntax inv2 && inv2.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } && Cs.EnclosingType(scope)?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(md => md.Identifier.Text == Cs.MemberName(inv2.Expression)) is { } helper && Regex.IsMatch(helper.ToString(), @"switch|TryGetValue|GetValueOrDefault|Contains\(|\.Keys|Allowed|ToLowerInvariant\(\)\s+switch")) return true;
            if (src is ElementAccessExpressionSyntax) return true;   // indexing a fixed table
            // `TryGetValue(key, out var column) ? column : "created"` style: the variable is an out of a dictionary lookup
            if (scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Cs.MemberName(i.Expression) == "TryGetValue" && i.ArgumentList.Arguments.Any(a => a.ToString().Contains("out var " + id.Identifier.Text) || a.ToString() == "out " + id.Identifier.Text))) return true;
        }
        return false;
    }

    private static bool IsConcatOrInterpolated(ExpressionSyntax e) => e is InterpolatedStringExpressionSyntax i && i.Contents.OfType<InterpolationSyntax>().Any() || e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression) || e is InvocationExpressionSyntax inv && Regex.IsMatch(inv.Expression.ToString(), @"string\.(Format|Concat|Join)$|\.Replace$|\.Append(Format)?$") || e is IdentifierNameSyntax;

    private static bool BuiltFromInput(SemanticModel model, ExpressionSyntax e, bool isWeb, SyntaxNode scope)
    {
        if (e is InterpolatedStringExpressionSyntax interp) return interp.Contents.OfType<InterpolationSyntax>().Any(h => !Harmless(model, h.Expression, scope) && Tainted(model, h.Expression, isWeb));
        if (e is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression)) { var ops = new List<ExpressionSyntax>(); void F(ExpressionSyntax x) { if (x is BinaryExpressionSyntax b2 && b2.IsKind(SyntaxKind.AddExpression)) { F(b2.Left); F(b2.Right); } else ops.Add(x); } F(bin); return ops.Any(o => o is not LiteralExpressionSyntax && !Harmless(model, o, scope) && Tainted(model, o, isWeb)); }
        if (e is InvocationExpressionSyntax) return IsConcatOrInterpolated(e) && Tainted(model, e, isWeb);
        if (e is IdentifierNameSyntax id)
        {
            var decl = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == id.Identifier.Text && v.Initializer is not null);
            var assigns = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left.ToString() == id.Identifier.Text).Select(a => a.Right).ToList();
            var sources = (decl is null ? Enumerable.Empty<ExpressionSyntax>() : new[] { decl.Initializer!.Value }).Concat(assigns).ToList();
            if (sources.Count == 0) return Tainted(model, e, isWeb);
            return sources.Any(s => IsConcatOrInterpolated(s) && Tainted(model, s, isWeb) || s is InvocationExpressionSyntax sb && Regex.IsMatch(sb.Expression.ToString(), @"\.ToString$") && sb.Expression.ToString().Contains("builder", StringComparison.OrdinalIgnoreCase) && scope.ToString().Contains(".Append(") && Tainted(model, scope.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Cs.MemberName(i.Expression) == "Append") ?? s, isWeb));
        }
        return false;
    }

    private static void Invocation(ScanContext ctx, List<Finding> findings, string rel, SemanticModel model, InvocationExpressionSyntax inv, bool isWeb)
    {
        var name = Cs.MemberName(inv.Expression); var receiver = Cs.ReceiverText(inv.Expression);
        var method = Cs.EnclosingMethod(inv); var scope = (SyntaxNode?)(method is null ? null : Cs.BodyOf(method)) ?? inv;
        var args = inv.ArgumentList.Arguments;
        // reflected markup: Content("<…{q}…>", "text/html")
        if (name is "Content" && args.Count >= 2 && args[1].ToString().Contains("text/html") && args[0].Expression is InterpolatedStringExpressionSyntax ci && ci.Contents.OfType<InterpolationSyntax>().Any(h => Tainted(model, h.Expression, isWeb) && !Regex.IsMatch(h.Expression.ToString(), @"Encode|Escape|Sanitiz")))
            findings.Add(new Finding("cross-site-scripting", "D29", "request text is interpolated into a text/html response without encoding: reflected XSS", rel, Cs.Line(inv), null, 2));
        if (name is "Select" or "SelectSingleNode" or "Evaluate" && (Regex.IsMatch(receiver, @"(?i)navigator|xpath") || Cs.TypeOf(model, (inv.Expression as MemberAccessExpressionSyntax)?.Expression ?? inv.Expression)?.Name is "XPathNavigator") && args.Count > 0 && BuiltFromInput(model, args[0].Expression, isWeb, scope))
            findings.Add(new Finding("xpath-injection", "D29", $"{name} with an XPath expression built from request input", rel, Cs.Line(inv), null, 2));
        if (Regex.IsMatch(name, @"^Log[A-Z]\w+$") && name is not ("LogInformation" or "LogWarning" or "LogError" or "LogDebug" or "LogTrace" or "LogCritical") && args.Any(a => Regex.IsMatch(a.ToString(), @"(?i)\.(Email|EmailAddress|Phone|PhoneNumber|Mobile|FullName|FirstName|LastName|DateOfBirth|Ssn|NationalId|Passport|Iban|CardNumber|HomeAddress|Street|Password|Secret|Token)\b|^(email|emailAddress|phone|phoneNumber|ssn|password|cardNumber|iban|dateOfBirth)$")))
            findings.Add(new Finding("sensitive-data-in-logs", "D32", $"{name}({Trunc(inv.ArgumentList.ToString(), 50)}) writes personal data to the log", rel, Cs.Line(inv), null, 2));
        if (Regex.IsMatch(inv.Expression.ToString(), @"^(MD5|SHA1)\.(HashData|HashDataAsync|TryHashData)$"))
        {
            var alg = inv.Expression.ToString()[..Regex.Match(inv.Expression.ToString(), @"^(MD5|SHA1)").Length];
            var body = scope.ToString() + Cs.TypeName(inv);
            if (Regex.IsMatch(body, @"(?i)password|passwd|pwd")) findings.Add(new Finding("insufficient-password-hashing", "D29", $"{alg} over a password: unsalted and fast, so a leaked table yields the passwords; use PBKDF2, bcrypt, scrypt or Argon2", rel, Cs.Line(inv), null, 2));
            else if (Regex.IsMatch(body, @"(?i)secret|token|signature|sign|hmac|auth|credential|encrypt|key")) findings.Add(new Finding("weak-hash-algorithm", "D29", $"{alg} used for a security purpose", rel, Cs.Line(inv), null, 1.5));
        }
        // SQL
        if (name is "FromSqlRaw" or "ExecuteSqlRaw" or "ExecuteSqlRawAsync" or "SqlQueryRaw" && args.Count > 0 && BuiltFromInput(model, args[0].Expression, isWeb, scope))
            findings.Add(new Finding("sql-injection", "D29", $"{name} with SQL built from request input: use FromSqlInterpolated or parameters", rel, Cs.Line(inv), null, 2));
        if (name is "ExecuteAsync" or "QueryAsync" or "QueryFirstOrDefaultAsync" or "QuerySingleAsync" or "ExecuteScalarAsync" or "ExecuteReaderAsync" or "Query" or "Execute" && args.Count > 0 && (receiver.Contains("onnection", StringComparison.Ordinal) || receiver.Contains("db", StringComparison.OrdinalIgnoreCase) || receiver.Contains("command", StringComparison.OrdinalIgnoreCase)) && BuiltFromInput(model, args[0].Expression, isWeb, scope))
            findings.Add(new Finding("sql-injection", "D29", $"{name} with SQL text built from request input", rel, Cs.Line(inv), null, 2));
        // command injection
        if (inv.Expression.ToString() is "Process.Start" && args.Count > 0 && (args.Count > 1 && BuiltFromInput(model, args[1].Expression, isWeb, scope) || BuiltFromInput(model, args[0].Expression, isWeb, scope)))
            findings.Add(new Finding("command-injection", "D29", "Process.Start with a command line built from request input", rel, Cs.Line(inv), null, 2));
        // path traversal
        if (inv.Expression.ToString() is "Path.Combine" or "Path.Join" && args.Skip(1).Any(a => Tainted(model, a.Expression, isWeb)))
        {
            var guarded = scope.ToString().Contains("GetFullPath") && Regex.IsMatch(scope.ToString(), @"StartsWith\(") || Regex.IsMatch(scope.ToString(), @"Path\.GetFileName\(|\.Contains\(""\.\.""\)|IsPathRooted|GetInvalidFileNameChars|\[A-Za-z0-9|Regex\.IsMatch");
            if (!guarded) findings.Add(new Finding("path-traversal", "D29", "a request-supplied segment is combined into a file path with no canonicalisation or root check", rel, Cs.Line(inv), null, 2));
        }
        if (Regex.IsMatch(inv.Expression.ToString(), @"^File\.(ReadAllText|ReadAllBytes|OpenRead|Open|WriteAllText|Delete|Exists)(Async)?$") && args.Count > 0 && BuiltFromInput(model, args[0].Expression, isWeb, scope) && !Regex.IsMatch(scope.ToString(), @"GetFullPath|GetFileName|IsPathRooted|\.Contains\(""\.\."""))
            findings.Add(new Finding("path-traversal", "D29", $"{inv.Expression} on a path built from request input", rel, Cs.Line(inv), null, 2));
        // deserialization
        if (receiver.Contains("BinaryFormatter") && name is "Deserialize" || inv.Expression.ToString().Contains("BinaryFormatter") || name == "Deserialize" && Regex.IsMatch(receiver, @"NetDataContractSerializer|SoapFormatter|LosFormatter|ObjectStateFormatter"))
            findings.Add(new Finding("insecure-deserialization", "D29", $"{receiver}.{name}: a formatter that lets the payload pick the types it instantiates", rel, Cs.Line(inv), null, 2));
        // XSS: raw markup from input
        if (name is "Raw" && receiver.EndsWith("Html") && args.Count > 0 && Tainted(model, args[0].Expression, isWeb) && !Regex.IsMatch(args[0].ToString(), @"Encode|Sanitiz"))
            findings.Add(new Finding("cross-site-scripting", "D29", "Html.Raw over request-derived text writes it to the page unescaped", rel, Cs.Line(inv), null, 2));
        if (name is "WriteAsync" or "Write" && Regex.IsMatch(receiver, @"Response(\.Body)?$") && args.Count > 0 && (args[0].Expression is InterpolatedStringExpressionSyntax || args[0].Expression is BinaryExpressionSyntax) && args[0].ToString().Contains('<') && Tainted(model, args[0].Expression, isWeb) && !Regex.IsMatch(args[0].ToString(), @"Encode|Escape"))
            findings.Add(new Finding("cross-site-scripting", "D29", "markup with request-derived text is written straight to the response", rel, Cs.Line(inv), null, 2));
        if (name is "Append" or "AppendLine" or "AppendFormat" && Regex.IsMatch(receiver, @"(?i)sql|query|command|cmd") && args.Count > 0 && BuiltFromInput(model, args[0].Expression, isWeb, scope))
            findings.Add(new Finding("sql-injection", "D29", "request input appended into SQL text", rel, Cs.Line(inv), null, 2));
        // ReDoS: Regex over input with no timeout
        if ((receiver is "Regex" && name is "IsMatch" or "Match" or "Matches" or "Replace" or "Split") && args.Count >= 2 && !args.Any(a => a.ToString().Contains("TimeSpan") || a.ToString().Contains("Timeout")) && Tainted(model, args[0].Expression, isWeb) && !scope.ToString().Contains("NonBacktracking"))
        {
            var pattern = args[1].ToString();
            var dangerous = Regex.IsMatch(pattern, @"\([^)]*[+*]\)[+*]|\(\.\*\)\+|\[\^?[^\]]*\][+*]\s*\)?[+*]|\(\w\+\)\+|\(\\\w\+\)\+|\(\\s\*\\w\+\)\*") || args[1].Expression is IdentifierNameSyntax;
            if (dangerous) findings.Add(new Finding("regex-denial-of-service", "D29", $"a backtracking-prone pattern ({Trunc(pattern, 40)}) runs over request input with no match timeout", rel, Cs.Line(inv), null, 2));
        }
        if (receiver is "Regex" && name is "IsMatch" or "Match" or "Matches" && args.Count >= 2 && args[1].Expression is IdentifierNameSyntax && Tainted(model, args[1].Expression, isWeb))
            findings.Add(new Finding("regex-denial-of-service", "D29", "the regular expression ITSELF comes from request input", rel, Cs.Line(inv), null, 2));
        // LDAP / XPath
        if (Regex.IsMatch(inv.Expression.ToString(), @"\.(SelectNodes|SelectSingleNode|XPathSelectElements?|XPathEvaluate|Evaluate|Compile)$") && args.Count > 0 && BuiltFromInput(model, args[0].Expression, isWeb, scope))
            findings.Add(new Finding("xpath-injection", "D29", $"{name} with an XPath expression built from request input", rel, Cs.Line(inv), null, 2));
        if (Regex.IsMatch(receiver, @"DirectorySearcher|LdapConnection|DirectoryEntry") || name is "FindOne" or "FindAll" or "SendRequest")
        {
            var filterAssign = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => a.Left.ToString().EndsWith("Filter") && BuiltFromInput(model, a.Right, isWeb, scope));
            if (filterAssign is not null) findings.Add(new Finding("ldap-injection", "D29", "an LDAP filter is built from request input without escaping", rel, Cs.Line(filterAssign), null, 2));
        }
        // SSRF
        if (Regex.IsMatch(name, @"^(GetAsync|GetStringAsync|GetStreamAsync|GetByteArrayAsync|PostAsync|PutAsync|DeleteAsync|SendAsync|GetFromJsonAsync|PostAsJsonAsync|DownloadStringAsync|OpenReadAsync)$") && args.Count > 0 && (Regex.IsMatch(receiver, @"(?i)http|client|web") || args[0].ToString().Contains("Uri")) && HostFromInput(model, args[0].Expression, isWeb, scope) && !Regex.IsMatch(scope.ToString(), @"IsLoopback|IsPrivate|AllowList|allowedHosts|Allowed|Whitelist|StartsWith\(""https://|Host\s*==|\.Host\b.*(Equals|==)"))
            findings.Add(new Finding("server-side-request-forgery", "D29", $"{name} fetches a URL whose host comes from request input: the server can be pointed at internal addresses", rel, Cs.Line(inv), null, 2));
        // open redirect
        if (name is "Redirect" or "RedirectPermanent" or "LocalRedirect" && args.Count > 0 && name != "LocalRedirect" && Tainted(model, args[0].Expression, isWeb, strict: true) && !Regex.IsMatch(scope.ToString(), @"IsLocalUrl|Uri\.IsWellFormedUriString|StartsWith\(""/""\)|\.Host\b|allowedReturn"))
            findings.Add(new Finding("open-redirect", "D29", "Redirect to a request-supplied URL without IsLocalUrl or an allow-list", rel, Cs.Line(inv), null, 2));
        // insecure randomness
        if (Regex.IsMatch(inv.Expression.ToString(), @"^(new Random\(\)|Random\.Shared|_?random|rng|rnd)\.Next(Bytes|Int64|Double)?$") || receiver is "Random.Shared")
        {
            var body = scope.ToString();
            if (Regex.IsMatch(body + " " + (method is null ? "" : Cs.NameOf(method)), @"(?i)token|secret|password|salt|nonce|otp|code|key|session|reset|verif|invite|csrf|api")) findings.Add(new Finding("insecure-randomness", "D29", "System.Random generates a security value; use RandomNumberGenerator", rel, Cs.Line(inv), null, 2));
        }
        // code injection: dynamic compilation / scripting of input
        if (Regex.IsMatch(inv.Expression.ToString(), @"CSharpScript\.(Evaluate|Run)|Eval(uate)?Async?$|ExecuteScript|RunScript|\.Compile\(") && args.Count > 0 && Tainted(model, args[0].Expression, isWeb))
            findings.Add(new Finding("code-injection", "D29", $"{name} evaluates request-supplied text as code", rel, Cs.Line(inv), null, 2));
        // log injection: user-controlled text written into a log without encoding (only where line breaks matter: structured templates are fine)
        if (Regex.IsMatch(name, @"^Log(Information|Warning|Error|Debug|Critical|Trace)$") && args.Count > 0 && args[0].Expression is InterpolatedStringExpressionSyntax li && li.Contents.OfType<InterpolationSyntax>().Any(h => Tainted(model, h.Expression, isWeb) && !Regex.IsMatch(h.Expression.ToString(), @"Replace\(|Sanitiz|Encode|\.Id\b|Count|Length")) && isWeb)
            findings.Add(new Finding("log-injection", "D29", "request-controlled text is interpolated into a log line: line breaks can forge entries", rel, Cs.Line(inv), null, 1));
        // error information exposure
        if (name is "Problem" or "BadRequest" or "StatusCode" or "WriteAsync" && args.Any(a => Regex.IsMatch(a.ToString(), @"\b\w*(ex|exception)\.(ToString\(\)|StackTrace)\b|\.StackTrace")) )
            findings.Add(new Finding("error-information-exposure", "D29", "an exception's stack trace is written into the HTTP response", rel, Cs.Line(inv), null, 1));
        if (name is "UseDeveloperExceptionPage" && !inv.Ancestors().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString().Contains("IsDevelopment")))
            findings.Add(new Finding("error-information-exposure", "D29", "UseDeveloperExceptionPage outside an IsDevelopment() guard shows stack traces to clients", rel, Cs.Line(inv), null, 1));
        // uncontrolled recursion / unbounded allocation live in Patterns (X17/X15)
    }

    /// <summary>SSRF needs the HOST to be chosen by the request: a whole URL from input, or input interpolated before the path begins. A relative path on a typed client cannot leave its base address.</summary>
    private static bool HostFromInput(SemanticModel model, ExpressionSyntax e, bool isWeb, SyntaxNode scope)
    {
        if (e is InterpolatedStringExpressionSyntax interp)
        {
            var first = interp.Contents.FirstOrDefault();
            if (first is InterpolatedStringTextSyntax t) { var lead = t.TextToken.Text; if (!lead.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false; if (Regex.IsMatch(lead, @"^https?://[^/]+/")) return false; }
            return interp.Contents.OfType<InterpolationSyntax>().Take(1).Any(h => Tainted(model, h.Expression, isWeb, strict: true));
        }
        if (e is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression)) { var ops = new List<ExpressionSyntax>(); void F(ExpressionSyntax x) { if (x is BinaryExpressionSyntax b2 && b2.IsKind(SyntaxKind.AddExpression)) { F(b2.Left); F(b2.Right); } else ops.Add(x); } F(bin); return ops[0] is LiteralExpressionSyntax l ? l.Token.ValueText.StartsWith("http") && !Regex.IsMatch(l.Token.ValueText, @"^https?://[^/]+/") && ops.Count > 1 && Tainted(model, ops[1], isWeb, strict: true) : Tainted(model, ops[0], isWeb, strict: true); }
        if (e is IdentifierNameSyntax || e is ObjectCreationExpressionSyntax { Type: var ut } oc2 && Cs.Simple(ut) == "Uri" && oc2.ArgumentList?.Arguments.Count > 0 && (oc2.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax || oc2.ArgumentList.Arguments[0].Expression is InterpolatedStringExpressionSyntax))
        {
            var inner = e is ObjectCreationExpressionSyntax o3 ? o3.ArgumentList!.Arguments[0].Expression : e;
            if (inner is InterpolatedStringExpressionSyntax) return HostFromInput(model, inner, isWeb, scope);
            var decl = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == inner.ToString() && v.Initializer is not null);
            if (decl is not null) return HostFromInput(model, decl.Initializer!.Value, isWeb, scope);
            return Tainted(model, inner, isWeb, strict: true);
        }
        return false;
    }

    private static void Creation(ScanContext ctx, List<Finding> findings, string rel, SemanticModel model, ObjectCreationExpressionSyntax oc, bool isWeb)
    {
        var type = Cs.Simple(oc.Type);
        var method = Cs.EnclosingMethod(oc); var scope = (SyntaxNode?)(method is null ? null : Cs.BodyOf(method)) ?? oc;
        if (type is "SqlCommand" or "NpgsqlCommand" or "MySqlCommand" or "SqliteCommand" or "OracleCommand" && oc.ArgumentList?.Arguments.Count > 0 && BuiltFromInput(model, oc.ArgumentList.Arguments[0].Expression, isWeb, scope))
            findings.Add(new Finding("sql-injection", "D29", $"new {type} with SQL text built from request input; use parameters", rel, Cs.Line(oc), null, 2));
        if (type is "BinaryFormatter" or "NetDataContractSerializer" or "SoapFormatter" or "LosFormatter" or "ObjectStateFormatter")
            findings.Add(new Finding("insecure-deserialization", "D29", $"{type} is unsafe for any input it does not fully trust", rel, Cs.Line(oc), null, 2));
        if (type is "XmlDocument" or "XmlTextReader" && scope.ToString().Contains("DtdProcessing.Parse"))
            findings.Add(new Finding("xml-external-entity", "D29", $"{type} with DTD processing enabled", rel, Cs.Line(oc), null, 2));
        if (type is "ProcessStartInfo" && oc.ArgumentList?.Arguments.Count > 0 && oc.ArgumentList.Arguments.Any(a => BuiltFromInput(model, a.Expression, isWeb, scope)) || type is "ProcessStartInfo" && oc.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().Any(a => a.Left.ToString() is "Arguments" or "FileName" && BuiltFromInput(model, a.Right, isWeb, scope)) == true)
            findings.Add(new Finding("command-injection", "D29", "ProcessStartInfo with a command line built from request input (ArgumentList keeps arguments apart)", rel, Cs.Line(oc), null, 2));
        if (type is "Regex" && oc.ArgumentList?.Arguments.Count >= 1 && oc.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax && !oc.ArgumentList.Arguments.Any(a => a.ToString().Contains("TimeSpan")) && Tainted(model, oc.ArgumentList.Arguments[0].Expression, isWeb))
            findings.Add(new Finding("regex-denial-of-service", "D29", "a Regex compiled from request input with no timeout", rel, Cs.Line(oc), null, 2));
        if (type is "MarkupString" && oc.ArgumentList?.Arguments.Count > 0 && Tainted(model, oc.ArgumentList.Arguments[0].Expression, isWeb) && !Regex.IsMatch(oc.ArgumentList.ToString(), @"Encode|Sanitiz"))
            findings.Add(new Finding("cross-site-scripting", "D29", "MarkupString wraps request-derived text as raw HTML", rel, Cs.Line(oc), null, 2));
        if (type is "HtmlString" && oc.ArgumentList?.Arguments.Count > 0 && Tainted(model, oc.ArgumentList.Arguments[0].Expression, isWeb) && !Regex.IsMatch(oc.ArgumentList.ToString(), @"Encode|Sanitiz"))
            findings.Add(new Finding("cross-site-scripting", "D29", "HtmlString wraps request-derived text as raw HTML", rel, Cs.Line(oc), null, 2));
    }

    private static void Interpolation(ScanContext ctx, List<Finding> findings, string rel, SemanticModel model, InterpolatedStringExpressionSyntax interp, bool isWeb)
    {
        // SQL text assembled by interpolation and later executed: catch the assembly site when the method also executes SQL
        var text = string.Concat(interp.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.Text));
        if (!Regex.IsMatch(text, @"(?i)\b(select|insert|update|delete|where|from|order by)\b")) return;
        if (!interp.Contents.OfType<InterpolationSyntax>().Any(h => Tainted(model, h.Expression, isWeb))) return;
        var method = Cs.EnclosingMethod(interp); var body = method is null ? "" : Cs.BodyOf(method)?.ToString() ?? "";
        if (interp.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax inv && Cs.MemberName(inv.Expression) is "FromSqlInterpolated" or "ExecuteSqlInterpolated" or "ExecuteSqlInterpolatedAsync" or "SqlQuery" or "FromSql" or "ExecuteSql" or "ExecuteSqlAsync") return;   // FormattableString parameterises
        if (Regex.IsMatch(body, @"FromSqlRaw|ExecuteSqlRaw|SqlQueryRaw|CommandText|QueryAsync|ExecuteAsync|SqlCommand|NpgsqlCommand|Execute\("))
            findings.Add(new Finding("sql-injection", "D29", $"SQL text interpolates request input: `{Trunc(interp.ToString(), 60)}`", rel, Cs.Line(interp), null, 2));
    }

    private static string Trunc(string s, int n) { s = Regex.Replace(s, @"\s+", " "); return s.Length <= n ? s : s[..n] + "…"; }

    // ---------------- D30 / D43 / D12 from the lock files and the OSV snapshot ----------------
    private static void Dependencies(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var resolved = new Dictionary<(string id, string version), List<(string file, int? line, bool direct, string project)>>();
        foreach (var p in repo.Projects)
        {
            var lockPath = (p.Directory.Length == 0 ? "" : p.Directory + "/") + "packages.lock.json";
            if (repo.Exists(lockPath))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(repo.Text(lockPath));
                    var lines = repo.Text(lockPath).Split('\n');
                    foreach (var tfm in doc.RootElement.GetProperty("dependencies").EnumerateObject())
                        foreach (var dep in tfm.Value.EnumerateObject())
                        {
                            var type = dep.Value.TryGetProperty("type", out var t) ? t.GetString() : "";
                            if (type == "Project") continue;
                            var version = dep.Value.TryGetProperty("resolved", out var rv) ? rv.GetString() : null;
                            if (version is null) continue;
                            var line = Array.FindIndex(lines, l => l.Contains($"\"{dep.Name}\"")) is var i and >= 0 ? i + 1 : (int?)null;
                            Add(resolved, dep.Name, version, lockPath, line, type == "Direct", p.Name);
                        }
                }
                catch (System.Text.Json.JsonException) { }
            }
            else foreach (var (id, version) in p.Packages.Where(x => x.version is not null))
            {
                var line = LineOf(repo.Text(p.Path), $"\"{id}\"");
                var file = line is null && repo.Exists("Directory.Packages.props") ? "Directory.Packages.props" : p.Path;
                Add(resolved, id, version!, file, line ?? LineOf(repo.Text(file), $"\"{id}\""), true, p.Name);
            }
        }
        if (resolved.Count == 0) { ctx.Skip("D30", "no NuGet dependencies declared"); ctx.Skip("D43", "no NuGet dependencies declared"); return; }
        var vulnerable = new List<Finding>(); var malicious = new List<Finding>(); var withdrawnSeen = 0;
        foreach (var ((id, version), sites) in resolved.OrderBy(k => k.Key.id, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Key.version))
        {
            if (!NuGetVersion.TryParse(version, out var v)) continue;
            foreach (var adv in ctx.Data.For(id).OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (adv.Withdrawn is not null) { withdrawnSeen++; continue; }
                if (!DataSnapshots.Affects(adv, id, v)) continue;
                var site = sites.OrderBy(s => s.direct ? 0 : 1).First();
                var alias = adv.Aliases.FirstOrDefault(a => a.StartsWith("CVE-")) ?? adv.Id;
                var fixedIn = adv.Affected.Where(a => string.Equals(a.Package, id, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.Ranges).SelectMany(r => r.Events).Select(e => e.fixedVersion).FirstOrDefault(f => f is not null);
                var severity = adv.Severity ?? "unknown";
                var weight = severity.ToUpperInvariant() switch { "CRITICAL" => 3, "HIGH" => 2, "MODERATE" or "MEDIUM" => 1, "LOW" => 0.5, _ => 1 };
                if (adv.IsMalicious)
                    malicious.Add(new Finding("malicious-dependency", "D43", $"{id} {version} is published as MALICIOUS ({adv.Id}): remove it and rotate every credential it could have read", site.file, site.line, site.line, 3) { Properties = new() { ["package"] = id, ["version"] = version, ["advisory"] = adv.Id } });
                else
                    vulnerable.Add(new Finding("vulnerable-dependency", "D30", $"{id} {version}{(site.direct ? "" : " (transitive)")} has a known vulnerability {alias} ({adv.Id}, {severity}){(fixedIn is not null ? $", fixed in {fixedIn}" : "")}: {Trunc(adv.Summary, 80)}", site.file, site.line, site.line, site.direct ? weight : weight * 0.75) { Properties = new() { ["package"] = id, ["version"] = version, ["advisory"] = adv.Id, ["severity"] = severity, ["direct"] = site.direct.ToString().ToLowerInvariant() } });
            }
        }
        foreach (var f in vulnerable) ctx.Add(f);
        foreach (var f in malicious) ctx.Add(f);
        var packages = resolved.Count;
        ctx.Measure("D30", Shape.FromFindings(vulnerable, Math.Max(1.0, packages / 25.0), 1.0), note: $"{vulnerable.Count} advisory match(es) across {packages} resolved packages ({resolved.Count(r => r.Value.Any(s => s.direct))} direct); OSV snapshot {ctx.Data.Provenance["osv-nuget.json"].Split(' ')[0]}");
        ctx.Measure("D43", malicious.Count == 0 ? 10 : 0, note: $"{malicious.Count} malicious package(s) among {packages}");
        ctx.Facts["dependencies"] = $"{packages} resolved ({resolved.Count(r => r.Value.Any(s => s.direct))} direct), {vulnerable.Count} vulnerable, {malicious.Count} malicious";
    }

    private static void Add(Dictionary<(string, string), List<(string, int?, bool, string)>> d, string id, string version, string file, int? line, bool direct, string project)
    {
        var key = (id.ToLowerInvariant(), version); if (!d.TryGetValue(key, out var l)) d[key] = l = new(); l.Add((file, line, direct, project));
        d[(id, version)] = l;
    }

    private static int? LineOf(string text, string needle) { var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); return i < 0 ? null : text.Take(i).Count(ch => ch == '\n') + 1; }

    // ---------------- D44 platform end-of-life ----------------
    private static void Platform(ScanContext ctx)
    {
        var repo = ctx.Repo; var asOf = ctx.Data.AsOf; var findings = new List<Finding>(); var checked_ = 0;
        EolCycle? Cycle(string major, string minor) => ctx.Data.DotnetCycles.FirstOrDefault(c => c.Cycle == $"{major}.{minor}" || c.Cycle == major && minor == "0" && int.Parse(major) >= 5);
        foreach (var p in repo.Projects)
        {
            foreach (var tfm in p.TargetFrameworks)
            {
                var m = Regex.Match(tfm, @"^net(?:coreapp)?(\d+)\.(\d+)");
                if (!m.Success) continue;
                checked_++;
                var cycle = Cycle(m.Groups[1].Value, m.Groups[2].Value);
                if (cycle?.Eol is { } eol && eol <= asOf)
                {
                    var line = LineOf(repo.Text(p.Path), tfm) ?? LineOf(repo.Text("Directory.Build.props"), tfm);
                    var file = LineOf(repo.Text(p.Path), tfm) is null && repo.Exists("Directory.Build.props") ? "Directory.Build.props" : p.Path;
                    findings.Add(new Finding("end-of-life-platform", "D44", $"{p.Name} targets {tfm}: .NET {cycle.Cycle} reached end of life on {eol:yyyy-MM-dd} and receives no security patches", file, line, line, 2) { Properties = new() { ["subject"] = tfm } });
                }
            }
        }
        if (repo.GlobalJsonSdk is { } sdk)
        {
            var m = Regex.Match(sdk, @"^(\d+)\.(\d+)");
            if (m.Success) { checked_++; var cycle = Cycle(m.Groups[1].Value, "0"); if (cycle?.Eol is { } eol && eol <= asOf) findings.Add(new Finding("end-of-life-platform", "D44", $"global.json pins SDK {sdk}: .NET {cycle.Cycle} reached end of life on {eol:yyyy-MM-dd}", "global.json", LineOf(repo.Text("global.json"), sdk), null, 2) { Properties = new() { ["subject"] = $"net{m.Groups[1].Value}.0" } }); }
        }
        foreach (var df in repo.Files.Where(f => Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = repo.Text(df).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s*FROM\s+\S*mcr\.microsoft\.com/dotnet/(?:aspnet|sdk|runtime|runtime-deps):(\d+)\.(\d+)");
                if (!m.Success) continue;
                checked_++;
                var cycle = Cycle(m.Groups[1].Value, m.Groups[2].Value);
                if (cycle?.Eol is { } eol && eol <= asOf) findings.Add(new Finding("end-of-life-platform", "D44", $"{df} runs on .NET {m.Groups[1].Value}.{m.Groups[2].Value}, end of life since {eol:yyyy-MM-dd}", df, i + 1, i + 1, 2) { Properties = new() { ["subject"] = $"net{m.Groups[1].Value}.{m.Groups[2].Value}" } });
            }
        }
        foreach (var f in findings.DistinctBy(f => (f.File, f.Line, f.Message))) ctx.Add(f);
        if (checked_ == 0) ctx.Skip("D44", "no .NET target framework, SDK pin or base image to check");
        else ctx.Measure("D44", findings.Count == 0 ? 10 : Math.Max(0, 10 - 4 * findings.Count), note: $"{findings.Count} end-of-life platform(s) among {checked_} checked (endoflife.date as of {asOf:yyyy-MM-dd})");
    }

    // ---------------- D37 disclosure policy ----------------
    private static void Disclosure(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var policy = repo.Files.FirstOrDefault(f => Regex.IsMatch(f, @"^(\.github/)?SECURITY\.md$", RegexOptions.IgnoreCase)) ?? repo.Files.FirstOrDefault(f => Regex.IsMatch(f, @"(^|/)\.well-known/security\.txt$|(^|/)security\.txt$", RegexOptions.IgnoreCase));
        if (policy is null) { ctx.Add(new Finding("vulnerability-disclosure-policy", "D37", "no SECURITY.md or security.txt: a finder has no documented way to report a vulnerability", null, null, null, 2)); ctx.Measure("D37", 0, note: "no policy file"); return; }
        var text = repo.Text(policy);
        var contact = Regex.IsMatch(text, @"(?i)mailto:|[\w.+-]+@[\w-]+\.[\w.-]+|https?://\S*(security|advisor|report|disclos|hackerone|bugcrowd)\S*|Contact:\s*\S+|private vulnerability reporting");
        var process = Regex.IsMatch(text, @"(?i)(within|response|acknowledg|triage|\d+\s*(business\s*)?days|timeline|coordinated|embargo|disclos)");
        var scope = Regex.IsMatch(text, @"(?i)supported versions|in scope|out of scope|scope");
        var checks = new (string, bool)[] { ("a policy file", true), ("a reporting contact", contact), ("how reports are handled / response times", process), ("scope or supported versions", scope) };
        foreach (var (name, ok) in checks) if (!ok) ctx.Add(new Finding("vulnerability-disclosure-policy", "D37", $"{Path.GetFileName(policy)} lacks {name}", policy, 1, 1, 1));
        ctx.Measure("D37", contact ? Shape.FromChecklist(checks.Count(c => c.Item2), checks.Length) : 3, note: $"{policy}: contact {contact}, process {process}, scope {scope}");
    }

    // ---------------- S1 web-security posture ----------------
    private static void WebPosture(ScanContext ctx)
    {
        var web = ctx.Workspace.Production.Where(p => p.Project.Role == ProjectRole.Web || p.Trees.Any(t => t.GetRoot().ToString().Contains("WebApplication.CreateBuilder") || t.GetRoot().ToString().Contains("UseRouting()"))).ToList();
        if (web.Count == 0) { ctx.Skip("S1", "no web application: no HTTP surface to harden"); return; }
        var text = string.Join("\n", web.SelectMany(p => p.Trees).Select(t => t.GetRoot().ToString()));
        var findings = new List<Finding>();
        var hsts = Regex.IsMatch(text, @"UseHsts\(\)"); var httpsRedirect = Regex.IsMatch(text, @"UseHttpsRedirection\(\)");
        var headers = Regex.IsMatch(text, @"X-Content-Type-Options|Content-Security-Policy|X-Frame-Options|Referrer-Policy|UseSecurityHeaders|AddSecurityHeaderPolicies");
        var validation = Regex.IsMatch(text, @"\[ApiController\]|FluentValidation|AddValidatorsFromAssembly|ModelState\.IsValid|\[Required\]|\[Range\(|\[StringLength|\[MaxLength|MiniValidation|AddValidation|ValidateDataAnnotations|\.Validate\(");
        var cookiesSecure = !Regex.IsMatch(text, @"new CookieOptions") || Regex.IsMatch(text, @"Secure\s*=\s*true|CookieSecurePolicy\.Always|HttpOnly\s*=\s*true|SameSite\s*=\s*SameSiteMode\.(Strict|Lax)");
        var authBeforeAuthz = !Regex.IsMatch(text, @"UseAuthorization\(\)[\s\S]{0,400}UseAuthentication\(\)");
        var crypto = !ctx.FindingsFor("D29").Any(f => f.RuleId is "weak-cryptographic-algorithm" or "weak-hash-algorithm" or "improper-certificate-validation" or "token-signature-or-expiry-not-validated");
        // concrete sites
        foreach (var pc in web) foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot(); var src = root.ToString();
            // HSTS configured but never applied: the gap is in the PIPELINE file, next to where UseHsts() belongs
            if (!hsts && Regex.IsMatch(text, @"AddHsts\(") && Regex.IsMatch(src, @"UseHttpsRedirection\(\)|UseRouting\(\)|UseAuthentication\(\)"))
            { var l = LineOf(src, "UseHttpsRedirection(") ?? LineOf(src, "UseRouting(") ?? LineOf(src, "UseAuthentication("); findings.Add(new Finding("https-enforcement", "S1", "HSTS is configured (AddHsts) but UseHsts() is never added to the pipeline: production responses never carry Strict-Transport-Security", rel, l, l, 2)); }
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Cs.MemberName(i.Expression) is "Map" or "MapWhen" && i.ArgumentList.Arguments.Count >= 2))
            {
                // a branch mapped before the security-headers middleware is added skips it
                var stmt = inv.Ancestors().OfType<StatementSyntax>().FirstOrDefault(); if (stmt is null) continue;
                var block = stmt.Parent as BlockSyntax; if (block is null) continue;
                var idx = block.Statements.IndexOf(stmt);
                var headersLater = block.Statements.Skip(idx + 1).Any(s => Regex.IsMatch(s.ToString(), @"SecurityHeaders|X-Content-Type-Options|Content-Security-Policy"));
                var headersEarlier = block.Statements.Take(idx).Any(s => Regex.IsMatch(s.ToString(), @"SecurityHeaders|X-Content-Type-Options|Content-Security-Policy"));
                if (headersLater && !headersEarlier) findings.Add(new Finding("security-response-headers", "S1", $"app.{Cs.MemberName(inv.Expression)}({Trunc(inv.ArgumentList.Arguments[0].ToString(), 20)}) branches off BEFORE the security-headers middleware is added: responses on that branch carry none of the headers", rel, Cs.Line(inv), null, 2));
            }
            foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => Cs.Simple(o.Type) == "CookieOptions"))
            {
                var init = oc.Initializer?.ToString() ?? "";
                var missing = new[] { ("Secure", @"Secure\s*=\s*true"), ("HttpOnly", @"HttpOnly\s*=\s*true"), ("SameSite", @"SameSite\s*=") }.Where(x => !Regex.IsMatch(init, x.Item2)).Select(x => x.Item1).ToList();
                if (missing.Count > 0 && !Regex.IsMatch(src, @"CookiePolicyOptions|MinimumSameSitePolicy|Secure\s*=\s*CookieSecurePolicy\.Always")) findings.Add(new Finding("insecure-cookie-flags", "S1", $"cookie options without {string.Join("/", missing)}", rel, Cs.Line(oc), null, 1));
            }
            // request models without validation, when the app otherwise validates
            if (validation && Regex.IsMatch(rel, @"(?i)/(Contracts|Requests|Models|Dtos)/"))
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => Regex.IsMatch(t.Identifier.Text, @"(Request|Command|Input|Dto)$")))
                {
                    var members = (t is RecordDeclarationSyntax r && r.ParameterList is not null ? r.ParameterList.Parameters.Select(pp => (type: Cs.Simple(pp.Type), attrs: pp.AttributeLists.ToString(), node: (SyntaxNode)pp)) : Enumerable.Empty<(string type, string attrs, SyntaxNode node)>()).Concat(t.Members.OfType<PropertyDeclarationSyntax>().Select(pd => (type: Cs.Simple(pd.Type), attrs: pd.AttributeLists.ToString(), node: (SyntaxNode)pd))).ToList();
                    var constrainable = members.Where(m => m.type is "string" or "int" or "long" or "decimal" or "double").ToList();
                    if (constrainable.Count >= 1 && members.All(m => m.attrs.Length == 0) && t.AttributeLists.Count == 0 && !ctx.Workspace.Production.Any(p => p.Trees.Any(tr => tr.GetRoot().ToString().Contains($"AbstractValidator<{t.Identifier.Text}>") || tr.GetRoot().ToString().Contains($"IValidator<{t.Identifier.Text}>"))))
                    { var loc = ctx.Loc(t); findings.Add(new Finding("inbound-input-validation", "S1", $"{t.Identifier.Text} carries {constrainable.Count} free-form field(s) and no validation attributes or validator, while the rest of the API validates its models", loc.File, loc.Line, loc.EndLine, 1)); }
                }
        }
        var injectionSites = ctx.FindingsFor("D29").Count(f => f.RuleId is "sql-injection" or "command-injection" or "path-traversal" or "ldap-injection" or "xpath-injection" or "server-side-request-forgery" or "regex-denial-of-service" or "cross-site-scripting" or "open-redirect" or "code-injection");
        var checks = new (string, bool)[] { ("HTTPS enforced (UseHttpsRedirection / UseHsts)", httpsRedirect || hsts), ("security response headers", headers), ("inbound model validation that constrains what reaches the sinks", validation && injectionSites < 3), ("secure cookie flags", cookiesSecure), ("authentication before authorization in the pipeline", authBeforeAuthz), ("no weak crypto / certificate shortcuts", crypto) };
        foreach (var (name, ok) in checks) if (!ok) findings.Add(new Finding(name.StartsWith("HTTPS") ? "https-enforcement" : name.StartsWith("security response") ? "security-response-headers" : name.StartsWith("inbound") ? "inbound-input-validation" : "security-response-headers", "S1", $"web posture: missing {name}", null, null, null, 1));
        foreach (var f in findings) ctx.Add(f);
        ctx.Measure("S1", Shape.FromChecklist(checks.Count(c => c.Item2), checks.Length) * (Shape.FromFindings(findings.Where(f => f.File is not null), ctx.ProductionKloc, 0.5) / 10), note: string.Join(", ", checks.Where(c => c.Item2).Select(c => c.Item1)) + $"; {findings.Count(f => f.File is not null)} located site(s)");
    }

    // ---------------- C1–C5, D32: personal data and the controls around it ----------------
    private static readonly Regex Pii = new(@"(?i)\b(email|e_?mail|phone|mobile|ssn|social_?security|national_?id|passport|date_?of_?birth|dob|birth_?date|first_?name|last_?name|full_?name|surname|given_?name|home_?address|street|postal_?code|zip_?code|iban|credit_?card|card_?number|cvv|ip_?address|salary|health|diagnosis|religion|ethnicity|gender|biometric)\b", RegexOptions.Compiled);

    private static void Compliance(ScanContext ctx)
    {
        var prodText = string.Join("\n", ctx.Workspace.ProductionTrees.Select(t => t.tree.GetRoot().ToString()));
        // PII inventory: entity/record members with personal-data names
        var piiMembers = new List<(string type, string member, string file, int line)>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
            foreach (var t in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var names = t.Members.OfType<PropertyDeclarationSyntax>().Select(pd => (pd.Identifier.Text, (SyntaxNode)pd)).Concat(t is RecordDeclarationSyntax r && r.ParameterList is not null ? r.ParameterList.Parameters.Select(pp => (pp.Identifier.Text, (SyntaxNode)pp)) : Enumerable.Empty<(string, SyntaxNode)>());
                foreach (var (n, node) in names) if (Pii.IsMatch(n) && !Regex.IsMatch(n, @"(?i)template|format|pattern|header|max|min|length|count|type|kind|address(es)?Service|Gender(Neutral)?Pronoun")) piiMembers.Add((t.Identifier.Text, n, ctx.Workspace.RelPath(tree.FilePath), Cs.Line(node)));
            }
        var hasPersonalData = piiMembers.Count >= 2;
        ctx.Facts["personalData"] = hasPersonalData ? $"{piiMembers.Count} personal-data field(s): {string.Join(", ", piiMembers.Select(m => $"{m.type}.{m.member}").Distinct().Take(8))}" : "none detected";
        // D32: personal data written to logs, URLs or query strings
        var d32 = new List<Finding>();
        var piiNames = piiMembers.Select(m => m.member).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Cs.MemberName(inv.Expression);
                var isLog = Regex.IsMatch(name, @"^Log(Trace|Debug|Information|Warning|Error|Critical)$|^(Information|Debug|Warning|Error|Verbose)$") && Cs.ReceiverText(inv.Expression).Contains("log", StringComparison.OrdinalIgnoreCase);
                if (isLog)
                {
                    var hit = inv.ArgumentList.Arguments.Skip(0).Select(a => a.ToString()).FirstOrDefault(a => Regex.IsMatch(a, @"(?i)\.(Email|EmailAddress|Phone|PhoneNumber|Mobile|FullName|FirstName|LastName|DateOfBirth|Ssn|NationalId|Passport|Iban|CardNumber|HomeAddress|Street|Password|Secret|Token)\b") || Regex.IsMatch(a, @"(?i)^\s*(email|phone|ssn|password|cardNumber|iban|dateOfBirth)\s*[,)]?$"));
                    if (hit is not null && !Regex.IsMatch(hit, @"Mask|Redact|Hash|Pseudonym|\.Id\b")) d32.Add(new Finding("sensitive-data-in-logs", "D32", $"{name} writes {Trunc(hit, 40)} to the log: personal data in log storage, outside every retention and access control the database has", rel, Cs.Line(inv), null, 2));
                }
            }
            foreach (var interp in tree.GetRoot().DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
            {
                // a query-string VALUE that is itself personal data (the key name alone proves nothing: `?email=true` is a switch)
                var inUrl = interp.Contents.OfType<InterpolatedStringTextSyntax>().Any(t => t.TextToken.Text.Contains('?') || t.TextToken.Text.Contains('&') || t.TextToken.Text.Contains('/'));
                if (!inUrl) continue;
                for (var hi = 0; hi < interp.Contents.Count; hi++)
                {
                    if (interp.Contents[hi] is not InterpolationSyntax hole) continue;
                    var prev = hi > 0 ? interp.Contents[hi - 1].ToString() : "";
                    if (!Regex.IsMatch(prev, @"[?&=/][^?&=]*$")) continue;
                    var expr = hole.Expression.ToString();
                    if (Regex.IsMatch(expr, @"(?i)(^|\.|\()\w*(email|e_?mail|phone|mobile|ssn|social|passport|iban|cardnumber|card_?no|dateofbirth|birth|fullname|firstname|lastname|password|secret|token|apikey)\w*\)?$") && !Regex.IsMatch(expr, @"(?i)flag|enabled|count|id$|hash|mask|redact"))
                    { d32.Add(new Finding("sensitive-data-in-url", "D32", $"{Trunc(interp.ToString(), 60)} puts {Trunc(expr, 30)} into a URL, where proxies, browsers and access logs keep it", rel, Cs.Line(interp), null, 2)); break; }
                }
            }
        }
        foreach (var f in d32) ctx.Add(f);
        ctx.Measure("D32", Shape.FromFindings(d32, ctx.ProductionKloc, 1.5), note: $"{d32.Count} personal-data exposure site(s)");
        if (!hasPersonalData)
        {
            foreach (var d in new[] { "C1", "C3", "C4", "C5" }) ctx.Skip(d, "no personal or sensitive data detected in the model: nothing to encrypt, audit, expire or erase");
        }
        else
        {
            var c1 = new (string, bool)[] { ("transport security (HTTPS / TLS required)", Regex.IsMatch(prodText, @"UseHttpsRedirection|UseHsts|RequireHttpsMetadata\s*=\s*true|SslMode=Require|Ssl Mode=Require|TrustServerCertificate=false|Encrypt=true")), ("encryption or protection of stored sensitive values", Regex.IsMatch(prodText, @"IDataProtector|IDataProtectionProvider|Aes\.|AesGcm|ProtectedData|EncryptedColumn|Always Encrypted|ValueConverter<string, string>.*Encrypt|HasConversion\(.*Encrypt|Pseudonym|Hash\(")), ("secrets held outside the code (vault, key management, environment)", Regex.IsMatch(prodText, @"KeyVault|SecretClient|AddAzureKeyVault|AWSSecretsManager|Vault|GetEnvironmentVariable|AddUserSecrets|DataProtection\.PersistKeysTo")) };
            var c2 = c1; // placeholder to keep structure simple
            var c3 = new (string, bool)[] { ("an audit interceptor or audit table for changes", Regex.IsMatch(prodText, @"SaveChangesInterceptor|AuditTrail|AuditLog|AuditEntry|Audit\.|IAuditable|CreatedBy|ModifiedBy|ChangedBy|History\b.*Table|Temporal")), ("who/when stamped on changes", Regex.IsMatch(prodText, @"CreatedBy|ModifiedBy|UpdatedBy|ChangedBy|Actor|PerformedBy|UserId.*(Created|Modified|Changed)")) };
            var c4 = new (string, bool)[] { ("a retention period or TTL", Regex.IsMatch(prodText, @"(?i)retention|TimeToLive|ttl\b|ExpiresAt|ExpireAfter|PurgeAfter|KeepFor")), ("a cleanup / purge job", Regex.IsMatch(prodText, @"(?i)(Purge|Cleanup|Clean|Sweep|Prune|Expire|Retention)\w*(Service|Worker|Job|Sweeper)|BackgroundService[\s\S]{0,600}(Delete|Remove|Purge)")) };
            var c5 = new (string, bool)[] { ("erasure of a person's data", Regex.IsMatch(prodText, @"(?i)Erase|Anonymi[sz]e|Forget|RightToBeForgotten|DeletePersonalData|RemovePersonalData|ScrubPersonal")), ("export / portability", Regex.IsMatch(prodText, @"(?i)Export(PersonalData|MyData|UserData|Member|Customer|Account)|Portability|DownloadPersonalData|TakeOut")), ("consent recorded and checked", Regex.IsMatch(prodText, @"(?i)Consent")) };
            void Posture(string dim, string rule, (string, bool)[] checks)
            {
                foreach (var (name, ok) in checks) if (!ok) ctx.Add(new Finding(rule, dim, $"personal data is handled but there is no {name}", null, null, null, 1));
                ctx.Measure(dim, Shape.FromChecklist(checks.Count(c => c.Item2), checks.Length), note: string.Join(", ", checks.Where(c => c.Item2).Select(c => c.Item1)));
            }
            Posture("C1", "data-encryption-controls", c1); Posture("C3", "audit-trail", c3); Posture("C4", "data-retention-policy", c4); Posture("C5", "data-subject-rights", c5);
        }
        // C2: authorization enforced by default on the HTTP surface
        var web = ctx.Workspace.Production.Where(p => p.Project.Role == ProjectRole.Web).ToList();
        if (web.Count == 0) { ctx.Skip("C2", "no web application: no endpoints to authorize"); return; }
        var webText = string.Join("\n", web.SelectMany(p => p.Trees).Select(t => t.GetRoot().ToString()));
        var fallback = Regex.IsMatch(webText, @"FallbackPolicy\s*=|RequireAuthorization\(\)\s*;?\s*$|\.RequireAuthorization\(\)|AddAuthorizationBuilder\(\)[\s\S]{0,200}SetFallbackPolicy|AuthorizeFilter|new AuthorizeAttribute") && Regex.IsMatch(webText, @"FallbackPolicy|SetFallbackPolicy|AuthorizeFilter|options\.FallbackPolicy");
        var hasAuth = Regex.IsMatch(webText, @"AddAuthentication|AddAuthorization|UseAuthorization");
        if (!hasAuth) { ctx.Skip("C2", "no authentication or authorization configured: this surface declares itself public (or is not an API)"); return; }
        var controllers = new List<(string name, string file, int line, bool authorized, bool anonymous)>();
        foreach (var pc in web) foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text.EndsWith("Controller") || c.BaseList?.Types.Any(b => Cs.Simple(b.Type) is "ControllerBase" or "Controller") == true))
            {
                var classAuth = Cs.HasAttribute(cls.AttributeLists, "Authorize"); var classAnon = Cs.HasAttribute(cls.AttributeLists, "AllowAnonymous");
                var actions = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && (Cs.HasAttribute(m.AttributeLists, "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "Route") || true)).ToList();
                var anyActionAuth = actions.Any(a => Cs.HasAttribute(a.AttributeLists, "Authorize"));
                var isHealth = Regex.IsMatch(cls.Identifier.Text, @"(?i)health|ping|status|version|metrics|openapi|swagger");
                controllers.Add((cls.Identifier.Text, rel, Cs.Line(cls.Identifier), classAuth || anyActionAuth && actions.All(a => Cs.HasAttribute(a.AttributeLists, "Authorize") || Cs.HasAttribute(a.AttributeLists, "AllowAnonymous")), classAnon || isHealth));
            }
            // minimal API groups
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Regex.IsMatch(Cs.MemberName(i.Expression), @"^Map(Get|Post|Put|Delete|Patch)$")))
            {
                var stmt = inv.Ancestors().OfType<StatementSyntax>().FirstOrDefault()?.ToString() ?? inv.ToString();
                var route = inv.ArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "";
                var isHealth = Regex.IsMatch(route, @"(?i)health|ping|status|version|metrics|openapi|swagger");
                var groupAuthorized = inv.Ancestors().OfType<StatementSyntax>().Any() && Regex.IsMatch(tree.GetRoot().ToString(), @"MapGroup\([^)]*\)[\s\S]{0,300}RequireAuthorization");
                controllers.Add(($"endpoint {Trunc(route, 30)}", rel, Cs.Line(inv), Regex.IsMatch(stmt, @"RequireAuthorization|\[Authorize") || groupAuthorized, Regex.IsMatch(stmt, @"AllowAnonymous") || isHealth));
            }
        }
        var unprotected = controllers.Where(c => !c.authorized && !c.anonymous && !fallback).ToList();
        foreach (var c in unprotected) ctx.Add(new Finding("missing-authorization", "C2", $"{c.name} carries no [Authorize] / RequireAuthorization and the application sets no fallback policy: reachable without credentials", c.file, c.line, c.line, 2));
        var share = controllers.Count == 0 ? 1 : (double)controllers.Count(c => c.authorized || c.anonymous || fallback) / controllers.Count;
        ctx.Measure("C2", Shape.FromShare(share) * (fallback ? 1 : 0.9), note: $"{controllers.Count(c => c.authorized)} of {controllers.Count} controllers/endpoints authorize explicitly, {controllers.Count(c => c.anonymous)} deliberately anonymous; fallback policy: {fallback}");
    }
}
