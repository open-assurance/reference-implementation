using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Cai.Reference.Engine;

/// <summary>
/// Accessibility: AC1 text alternatives, AC2 forms and labels, AC3 page structure, AC4 keyboard semantics, AC5 ARIA
/// correctness, AC6 visual and motion safety, AC7 enforcement — static markup readiness read from .razor/.cshtml/.html
/// with the Razor code stripped, plus the repository's own CSS. Also reports MarkupString XSS in Razor markup (D29).
/// </summary>
public static class Accessibility
{
    private static readonly string[] Dims = { "AC1", "AC2", "AC3", "AC4", "AC5", "AC6", "AC7" };

    private sealed record Page(string File, IDocument Doc, string Text, string[] Lines, bool IsLayout, bool IsHost, string Raw);

    public static void Run(ScanContext ctx)
    {
        var repo = ctx.Repo;
        // shipped markup only: vendored libraries, build output, and design artefacts (mockups, wireframes, prototypes, documentation sites) are not the product's pages
        var markupFiles = repo.Files.Where(f => Regex.IsMatch(f, @"\.(razor|cshtml|html|htm)$", RegexOptions.IgnoreCase) && !Regex.IsMatch(f, @"(?i)(^|/)(wwwroot/lib|lib|node_modules|bin|obj|_framework|dist|mockups?|wireframes?|prototypes?|design|docs?|coverage|TestResults)/|_Imports\.razor$|_ViewImports\.cshtml$|_ViewStart\.cshtml$")).ToList();
        if (markupFiles.Count == 0) { foreach (var d in Dims) ctx.Skip(d, "lens not applicable: no HTML, Razor or Blazor markup in the repository"); return; }
        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true, IsNotConsumingCharacterReferences = true });
        var pages = new List<Page>();
        foreach (var f in markupFiles)
        {
            var raw = repo.Text(f); if (raw.Length == 0) continue;
            var stripped = StripRazor(raw);
            var doc = parser.ParseDocument(stripped);
            pages.Add(new Page(f, doc, stripped, raw.Split('\n'), Regex.IsMatch(f, @"(?i)layout"), Regex.IsMatch(f, @"(?i)(^|/)(App\.razor|_Host\.cshtml|_Layout\.cshtml|index\.html|wwwroot/index\.html)$") || stripped.Contains("<html", StringComparison.OrdinalIgnoreCase), raw.Replace("@@", "@")));
        }
        var css = repo.Files.Where(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(f, @"(?i)(^|/)(wwwroot/lib|lib|node_modules|bin|obj|dist)/|\.min\.css$|bootstrap|tailwind")).Select(f => (file: f, text: repo.Text(f))).ToList();
        foreach (var p in pages) foreach (Match m in Regex.Matches(p.Raw, @"<style[^>]*>([\s\S]*?)</style>", RegexOptions.IgnoreCase)) css.Add((p.File, m.Groups[1].Value));   // from the raw text: Razor stripping drops lone `}` lines, which also close CSS rules
        TextAlternatives(ctx, pages); Forms(ctx, pages); Structure(ctx, pages); Keyboard(ctx, pages, css); Aria(ctx, pages); Visual(ctx, pages, css); Enforcement(ctx); RazorXss(ctx, pages);
        var kpages = Math.Max(1.0, pages.Count / 10.0);
        foreach (var d in Dims.Take(6)) ctx.Measure(d, Shape.FromFindings(ctx.FindingsFor(d), kpages), note: $"{ctx.FindingsFor(d).Count()} site(s) across {pages.Count} markup files");
    }

    /// <summary>Removes Razor code so the HTML parser sees markup: @code/@functions blocks, @{ } statements, directive lines; expressions stay as text.</summary>
    public static string StripRazor(string text)
    {
        text = text.Replace("@@", "@");   // Razor's escape for a literal @ (e.g. @@keyframes, @@media inside <style>)
        var sb = new System.Text.StringBuilder(text);
        foreach (var kw in new[] { "@code", "@functions" })
        {
            int idx;
            while ((idx = sb.ToString().IndexOf(kw, StringComparison.Ordinal)) >= 0)
            {
                var s = sb.ToString(); var open = s.IndexOf('{', idx); if (open < 0) break;
                var depth = 0; var end = open;
                for (; end < s.Length; end++) { if (s[end] == '{') depth++; else if (s[end] == '}') { depth--; if (depth == 0) break; } }
                // keep line count: replace with newlines
                var removed = s.Substring(idx, Math.Min(end + 1, s.Length) - idx);
                sb.Remove(idx, removed.Length); sb.Insert(idx, new string('\n', removed.Count(c => c == '\n')));
            }
        }
        var t = sb.ToString();
        t = Regex.Replace(t, @"(?m)^\s*@(page|using|inject|inherits|implements|layout|model|namespace|attribute|typeparam|rendermode|addTagHelper|preservewhitespace)\b[^\n]*$", m => new string(' ', m.Value.Length));
        t = Regex.Replace(t, @"@\{[^{}]*\}", m => Regex.Replace(m.Value, @"[^\n]", " "));
        // control-flow lines: keep inner markup, drop the keywords and braces
        t = Regex.Replace(t, @"(?m)^(\s*)@(if|else if|else|foreach|for|while|switch|try|catch|finally|lock|using)\b[^\n{]*\{?\s*$", "$1");
        t = Regex.Replace(t, @"(?m)^\s*\}\s*(else\s*\{?)?\s*$", "");
        t = Regex.Replace(t, @"(?m)^\s*(case [^:]+:|default:|break;)\s*$", "");
        return t;
    }

    private static int Line(Page p, IElement e) => e.SourceReference?.Position.Line ?? FindLine(p, e.OuterHtml.Split('\n')[0].Trim());
    private static int FindLine(Page p, string snippet) { for (var i = 0; i < p.Lines.Length; i++) if (p.Lines[i].Contains(snippet.Length > 30 ? snippet[..30] : snippet, StringComparison.Ordinal)) return i + 1; return 1; }
    private static bool Has(IElement e, string attr) => e.HasAttribute(attr) && !string.IsNullOrWhiteSpace(e.GetAttribute(attr));
    private static bool HasAny(IElement e, params string[] attrs) => attrs.Any(a => Has(e, a));
    private static bool Hidden(IElement e) => e.GetAttribute("aria-hidden") == "true" || e.GetAttribute("role") is "presentation" or "none";
    private static string TextOf(IElement e) => Regex.Replace(e.TextContent, @"\s+", " ").Trim();
    private static bool HasName(IElement e) => TextOf(e).Length > 0 && !Regex.IsMatch(TextOf(e), @"^@[\w.()]+$") == false || TextOf(e).Length > 0 || HasAny(e, "aria-label", "aria-labelledby", "title") || e.QuerySelectorAll("img[alt],svg[aria-label],svg title,[aria-label]").Any(c => !Hidden(c) && (c.TagName.ToLowerInvariant() != "img" || !string.IsNullOrWhiteSpace(c.GetAttribute("alt"))));
    private static bool HasAccessibleName(IElement e)
    {
        if (HasAny(e, "aria-label", "aria-labelledby", "title")) return true;
        var text = TextOf(e);
        if (text.Length > 0) return true;   // a Razor expression (@Title) counts as text the component supplies
        foreach (var c in e.QuerySelectorAll("*"))
        {
            if (Hidden(c)) continue;
            if (c.TagName.Equals("img", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.GetAttribute("alt"))) return true;
            if (c.TagName.Equals("svg", StringComparison.OrdinalIgnoreCase) && (HasAny(c, "aria-label", "aria-labelledby") || c.QuerySelector("title") is not null)) return true;
            if (HasAny(c, "aria-label")) return true;
        }
        return false;
    }
    private static bool IsRazorExpression(string? v) => v is not null && v.Contains('@');
    /// <summary>An anchor has a target when it carries href or a Razor Pages / MVC tag-helper attribute that emits one (asp-page, asp-action, asp-controller, asp-route-*).</summary>
    private static bool HasLinkTarget(IElement a) => a.HasAttribute("href") || a.Attributes.Any(at => at.Name.StartsWith("asp-", StringComparison.OrdinalIgnoreCase));

    // ---------------- AC1 ----------------
    private static void TextAlternatives(ScanContext ctx, List<Page> pages)
    {
        foreach (var p in pages)
        {
            foreach (var img in p.Doc.QuerySelectorAll("img"))
            {
                if (Hidden(img)) continue;
                if (!img.HasAttribute("alt") && !HasAny(img, "aria-label", "aria-labelledby")) ctx.Add(new Finding("missing-text-alternative", "AC1", $"<img src=\"{Trunc(img.GetAttribute("src"), 40)}\"> has no alt attribute (alt=\"\" marks decoration; a description serves content)", p.File, Line(p, img), null, 1));
            }
            foreach (var area in p.Doc.QuerySelectorAll("area[href], input[type=image]")) if (!Has(area, "alt") && !HasAny(area, "aria-label")) ctx.Add(new Finding("missing-text-alternative", "AC1", $"<{area.TagName.ToLowerInvariant()}> without alt", p.File, Line(p, area), null, 1));
            foreach (var svg in p.Doc.QuerySelectorAll("svg"))
            {
                if (Hidden(svg) || svg.ParentElement is { } pe && (pe.TagName.Equals("button", StringComparison.OrdinalIgnoreCase) || pe.TagName.Equals("a", StringComparison.OrdinalIgnoreCase)) && HasAny(pe, "aria-label", "aria-labelledby")) continue;
                if (svg.QuerySelector("title") is null && !HasAny(svg, "aria-label", "aria-labelledby") && svg.GetAttribute("role") != "img" && svg.ParentElement?.TextContent.Trim().Length == 0)
                    ctx.Add(new Finding("missing-text-alternative", "AC1", "a meaningful <svg> carries no <title>, aria-label or aria-hidden", p.File, Line(p, svg), null, 0.5));
            }
            foreach (var video in p.Doc.QuerySelectorAll("video"))
            {
                var muted = video.HasAttribute("muted");
                if (!muted && video.QuerySelector("track[kind=captions], track[kind=subtitles]") is null) ctx.Add(new Finding("missing-text-alternative", "AC1", "<video> with audio has no captions <track>", p.File, Line(p, video), null, 1));
                if (video.HasAttribute("autoplay") && !video.HasAttribute("controls") && !Regex.IsMatch(p.Text, @"(?i)pause|\.play\(\)|playing|reduced-motion", RegexOptions.None)) ctx.Add(new Finding("autoplay-media-without-control", "AC1", $"<video autoplay{(video.HasAttribute("loop") ? " loop" : "")}> plays with no controls and no pause mechanism", p.File, Line(p, video), null, 1));
            }
            foreach (var audio in p.Doc.QuerySelectorAll("audio[autoplay]")) if (!audio.HasAttribute("controls")) ctx.Add(new Finding("autoplay-media-without-control", "AC1", "<audio autoplay> with no controls", p.File, Line(p, audio), null, 1));
            foreach (var obj in p.Doc.QuerySelectorAll("object, embed, canvas")) if (!HasAny(obj, "aria-label", "aria-labelledby", "title") && TextOf(obj).Length == 0 && !Hidden(obj)) ctx.Add(new Finding("missing-text-alternative", "AC1", $"<{obj.TagName.ToLowerInvariant()}> has neither a name nor fallback content", p.File, Line(p, obj), null, 0.5));
        }
    }

    // ---------------- AC2 ----------------
    private static readonly Regex KnownFieldComponent = new(@"^(MudTextField|MudSelect|MudAutocomplete|MudCheckBox|MudSwitch|MudDatePicker|MudNumericField|FluentTextField|FluentSelect|FluentCheckbox|FluentNumberField|FluentTextArea|RadzenTextBox|RadzenDropDown|RadzenCheckBox|RadzenNumeric|TelerikTextBox|SfTextBox|InputText|InputNumber|InputSelect|InputTextArea|InputDate|InputCheckbox)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void Forms(ScanContext ctx, List<Page> pages)
    {
        foreach (var p in pages)
        {
            // <label for>, asp-for on a label (emits for= at render time), and label-like components (<Label For=...>, <MudInputLabel ForId>, <FluentLabel for>) that render one
            var labelsFor = p.Doc.QuerySelectorAll("*").Where(l => l.TagName.Equals("label", StringComparison.OrdinalIgnoreCase) || l.TagName.EndsWith("label", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.GetAttribute("for") ?? l.GetAttribute("asp-for") ?? l.GetAttribute("forid")).Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.Ordinal);
            foreach (var ctl in p.Doc.QuerySelectorAll("input, select, textarea"))
            {
                var type = ctl.GetAttribute("type")?.ToLowerInvariant() ?? "text";
                if (type is "hidden" or "submit" or "button" or "reset" or "image") continue;
                if (Hidden(ctl)) continue;
                var id = ctl.GetAttribute("id") ?? ctl.GetAttribute("asp-for");
                var labelled = id is not null && (labelsFor.Contains(id) || IsRazorExpression(id)) || ctl.Ancestors<IElement>().Any(a => a.TagName.Equals("label", StringComparison.OrdinalIgnoreCase)) || HasAny(ctl, "aria-label", "aria-labelledby", "title");
                if (!labelled && !IsRazorExpression(ctl.GetAttribute("aria-label")))
                    ctx.Add(new Finding("form-control-without-label", "AC2", $"<{ctl.TagName.ToLowerInvariant()}{(type != "text" ? $" type={type}" : "")}> has no programmatic label{(Has(ctl, "placeholder") ? " (a placeholder is not a label: it disappears on input and is not announced as the name)" : "")}", p.File, Line(p, ctl), null, 1));
            }
            foreach (var comp in p.Doc.QuerySelectorAll("*").Where(e => KnownFieldComponent.IsMatch(e.TagName)))
            {
                var id = comp.GetAttribute("id");
                var labelled = HasAny(comp, "label", "aria-label", "placeholder") && !comp.TagName.StartsWith("Input", StringComparison.OrdinalIgnoreCase) || id is not null && (labelsFor.Contains(id) || IsRazorExpression(id)) || HasAny(comp, "aria-label", "aria-labelledby") || comp.Ancestors<IElement>().Any(a => a.TagName.Equals("label", StringComparison.OrdinalIgnoreCase));
                if (!labelled && comp.TagName.StartsWith("Input", StringComparison.OrdinalIgnoreCase)) ctx.Add(new Finding("form-control-without-label", "AC2", $"<{comp.TagName}> field component has no associated <label for>, aria-label or wrapping label", p.File, Line(p, comp), null, 1));
                else if (!labelled) ctx.Add(new Finding("form-control-without-label", "AC2", $"<{comp.TagName}> carries no Label", p.File, Line(p, comp), null, 1));
            }
            foreach (var btn in p.Doc.QuerySelectorAll("button, [role=button]"))
                if (!HasAccessibleName(btn) && !Hidden(btn)) ctx.Add(new Finding("form-control-without-label", "AC2", $"<{btn.TagName.ToLowerInvariant()}> has no accessible name: no text, aria-label, aria-labelledby or title{(btn.QuerySelector("svg[aria-hidden=true]") is not null ? " (its only content is an aria-hidden icon)" : "")}", p.File, Line(p, btn), null, 1));
            foreach (var a in p.Doc.QuerySelectorAll("a").Where(HasLinkTarget))
                if (!HasAccessibleName(a) && !Hidden(a)) ctx.Add(new Finding("form-control-without-label", "AC2", $"<a href=\"{Trunc(a.GetAttribute("href"), 30)}\"> has no accessible name", p.File, Line(p, a), null, 1));
            foreach (var fs in p.Doc.QuerySelectorAll("fieldset")) if (fs.QuerySelector("legend") is null || TextOf(fs.QuerySelector("legend")!).Length == 0) ctx.Add(new Finding("form-control-without-label", "AC2", "<fieldset> without a non-empty <legend>", p.File, Line(p, fs), null, 0.5));
            // error messages not associated with their field
            foreach (var err in p.Doc.QuerySelectorAll("p, span, div, small").Where(e => Regex.IsMatch(e.GetAttribute("class") ?? "", @"(?i)\b(error|invalid|validation|field-message|help-error)") && !e.TagName.Equals("ValidationMessage", StringComparison.OrdinalIgnoreCase) && TextOf(e).Length > 0))
            {
                var id = err.GetAttribute("id");
                var referenced = id is not null && p.Doc.QuerySelectorAll("[aria-describedby],[aria-errormessage]").Any(c => (c.GetAttribute("aria-describedby") ?? "").Split(' ').Contains(id) || c.GetAttribute("aria-errormessage") == id);
                if (referenced || err.GetAttribute("role") == "alert") continue;   // a live region is announced on its own
                // the field directly before it, which names no description at all (a Razor expression in aria-describedby is a dynamic association)
                var prevField = err.PreviousElementSibling is { } ps && (ps.TagName is "INPUT" or "SELECT" or "TEXTAREA" || KnownFieldComponent.IsMatch(ps.TagName)) ? ps : null;
                if (prevField is not null && !prevField.HasAttribute("aria-describedby") && !prevField.HasAttribute("aria-errormessage")) ctx.Add(new Finding("form-error-not-associated", "AC2", $"the error message \"{Trunc(TextOf(err), 40)}\" follows a field but nothing ties them together: the field needs aria-describedby/aria-errormessage pointing at it", p.File, Line(p, err), null, 1));
            }
        }
    }

    // ---------------- AC3 ----------------
    private static void Structure(ScanContext ctx, List<Page> pages)
    {
        var mains = 0; var routedPages = pages.Where(p => Regex.IsMatch(string.Join("\n", p.Lines), @"(?m)^\s*@page\s")).ToList();
        foreach (var p in pages)
        {
            var html = p.Doc.QuerySelector("html");
            if (html is not null && p.Text.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                var lang = html.GetAttribute("lang");
                if (lang is null) ctx.Add(new Finding("page-structure-violation", "AC3", "<html> declares no lang attribute: assistive technology cannot choose pronunciation rules", p.File, FindLine(p, "<html"), null, 2));
                else if (!IsRazorExpression(lang) && !Regex.IsMatch(lang, @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")) ctx.Add(new Finding("page-structure-violation", "AC3", $"<html lang=\"{lang}\"> is not a well-formed BCP-47 tag", p.File, FindLine(p, "<html"), null, 1));
                var title = p.Doc.QuerySelector("title");
                if (title is null && !Regex.IsMatch(p.Text, @"<HeadOutlet|<PageTitle|ViewData\[""Title""\]|@ViewBag\.Title|RenderSection\(""Title""")) ctx.Add(new Finding("page-structure-violation", "AC3", "the document has no <title> and no title slot", p.File, FindLine(p, "<head"), null, 1));
                else if (title is not null && TextOf(title).Length == 0) ctx.Add(new Finding("page-structure-violation", "AC3", "<title> is empty", p.File, Line(p, title), null, 1));
                var vp = p.Doc.QuerySelector("meta[name=viewport]");
                if (vp is not null && Regex.IsMatch(vp.GetAttribute("content") ?? "", @"user-scalable\s*=\s*(no|0)|maximum-scale\s*=\s*1(\.0)?\b")) ctx.Add(new Finding("page-structure-violation", "AC3", "the viewport meta disables zoom", p.File, Line(p, vp), null, 2));
                foreach (var mr in p.Doc.QuerySelectorAll("meta[http-equiv]").Where(m => m.GetAttribute("http-equiv")?.Equals("refresh", StringComparison.OrdinalIgnoreCase) == true)) ctx.Add(new Finding("page-structure-violation", "AC3", "meta refresh redirects or reloads without user control", p.File, Line(p, mr), null, 1));
            }
            var mainCount = p.Doc.QuerySelectorAll("main, [role=main]").Length; mains += mainCount;
            if (mainCount > 1) ctx.Add(new Finding("page-structure-violation", "AC3", $"{mainCount} <main> landmarks in one document", p.File, Line(p, p.Doc.QuerySelectorAll("main, [role=main]")[1]), null, 1));
            // heading order: a skip inside ONE file where the earlier level is present; a component that starts at h3 composes into a page
            var headings = p.Doc.QuerySelectorAll("h1,h2,h3,h4,h5,h6").Where(h => !Hidden(h)).ToList();
            var prevLevel = 0;
            foreach (var h in headings)
            {
                var level = h.TagName[1] - '0';
                if (TextOf(h).Length == 0 && h.QuerySelector("img[alt]") is null) ctx.Add(new Finding("page-structure-violation", "AC3", $"<{h.TagName.ToLowerInvariant()}> is empty", p.File, Line(p, h), null, 1));
                if (prevLevel > 0 && level > prevLevel + 1) ctx.Add(new Finding("page-structure-violation", "AC3", $"heading level skips from h{prevLevel} to h{level}: heading navigation presents a hole", p.File, Line(p, h), null, 1));
                prevLevel = level;
            }
            foreach (var iframe in p.Doc.QuerySelectorAll("iframe")) if (!Has(iframe, "title")) ctx.Add(new Finding("page-structure-violation", "AC3", "<iframe> without a title", p.File, Line(p, iframe), null, 1));
            foreach (var table in p.Doc.QuerySelectorAll("table"))
            {
                var rows = table.QuerySelectorAll("tr").Length;
                if (rows >= 2 && table.QuerySelector("th") is null && table.GetAttribute("role") is not ("presentation" or "none")) ctx.Add(new Finding("page-structure-violation", "AC3", "a data table with no header cells (<th>)", p.File, Line(p, table), null, 1));
            }
        }
        if (routedPages.Count > 0 && mains == 0 && pages.Any(p => p.IsLayout)) ctx.Add(new Finding("page-structure-violation", "AC3", "no <main> landmark in any layout or page", pages.First(p => p.IsLayout).File, 1, null, 1));
    }

    // ---------------- AC4 ----------------
    private static readonly string[] NonInteractive = { "div", "span", "li", "p", "td", "tr", "img", "section", "article", "label", "i", "b", "strong", "em", "svg", "path", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "nav", "header", "footer", "figure" };

    private static void Keyboard(ScanContext ctx, List<Page> pages, List<(string file, string text)> css)
    {
        var pointerClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in css) foreach (Match m in Regex.Matches(text, @"([^{}]+)\{[^{}]*cursor\s*:\s*pointer[^{}]*\}")) foreach (Match cls in Regex.Matches(m.Groups[1].Value, @"\.([\w-]+)")) pointerClasses.Add(cls.Groups[1].Value);
        foreach (var p in pages)
        {
            foreach (var e in p.Doc.QuerySelectorAll("*"))
            {
                var tag = e.TagName.ToLowerInvariant();
                var attrs = e.Attributes.Select(a => a.Name.ToLowerInvariant()).ToList();
                var hasClick = attrs.Any(a => a is "onclick" or "@onclick" or "@ondblclick" or "ondblclick" || a.StartsWith("@onclick"));
                var keyHandler = attrs.Any(a => Regex.IsMatch(a, @"^@?onkey(down|up|press)"));
                var role = e.GetAttribute("role"); var tabindex = e.GetAttribute("tabindex");
                var focusable = tag is "a" && HasLinkTarget(e) || tag is "button" or "input" or "select" or "textarea" or "summary" || tabindex is not null && tabindex != "-1" || e.HasAttribute("contenteditable");
                if (hasClick && NonInteractive.Contains(tag))
                {
                    if (role is null || tabindex is null || !keyHandler)
                        ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", $"<{tag}> with a click handler lacks {string.Join(", ", new[] { role is null ? "a role" : null, tabindex is null ? "tabindex" : null, !keyHandler ? "a key handler" : null }.Where(x => x is not null))}: mouse-only", p.File, Line(p, e), null, 1));
                }
                var cls = (e.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pointer = cls.Any(pointerClasses.Contains) || Regex.IsMatch(e.GetAttribute("style") ?? "", @"cursor\s*:\s*pointer");
                if (pointer && !hasClick && NonInteractive.Contains(tag) && role is null && tabindex is null && !keyHandler && !e.QuerySelectorAll("a[href],button,input").Any())
                    ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", $"<{tag}> is styled `cursor: pointer` but has no role, tabindex or key handler: it looks clickable to mouse users only", p.File, Line(p, e), null, 0.5));
                if (attrs.Any(a => Regex.IsMatch(a, @"^@?onmouse(enter|leave|over|out)$")) && !hasClick && !focusable && !attrs.Any(a => Regex.IsMatch(a, @"^@?onfocus")))
                    ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", $"<{tag}> reacts only to the pointer entering/leaving and cannot take focus", p.File, Line(p, e), null, 0.5));
                if (tabindex is not null && int.TryParse(tabindex, out var ti) && ti > 0) ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", $"tabindex=\"{ti}\": a positive tabindex fights the document order and pulls this control ahead of the skip link and navigation", p.File, Line(p, e), null, 1));
                if (tag == "a")
                {
                    var href = e.GetAttribute("href") ?? (HasLinkTarget(e) ? "~tag-helper" : null);
                    if (href is null && !e.HasAttribute("@onclick") && !e.HasAttribute("onclick") && role is null) { if (TextOf(e).Length > 0) ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", "<a> without href is not focusable or activatable from the keyboard", p.File, Line(p, e), null, 0.5)); }
                    else if (href is not null && (href.Trim() == "#" || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) && hasClick)
                        ctx.Add(new Finding("non-keyboard-accessible-interaction", "AC4", $"<a href=\"{href}\"> with a click handler acts as a button: announced as a link, not activated by Space; use <button type=\"button\">", p.File, Line(p, e), null, 1));
                }
                // modal focus management
                if ((role == "dialog" || role == "alertdialog" || e.GetAttribute("aria-modal") == "true") && tag != "dialog")
                {
                    var file = string.Join("\n", p.Lines) + (ctx.Repo.Exists(p.File + ".cs") ? ctx.Repo.Text(p.File + ".cs") : "");
                    var managed = Regex.IsMatch(file, @"FocusAsync|autofocus|FocusTrap|focus-trap|inert|showModal|\.focus\(\)|InitialFocus|RestoreFocus|@onkeydown[^>]*Escape|Escape");
                    if (!managed || e.GetAttribute("aria-modal") is null) ctx.Add(new Finding("modal-focus-not-managed", "AC4", $"hand-rolled modal (<{tag} role=\"{role}\">){(e.GetAttribute("aria-modal") is null ? " without aria-modal" : "")}{(!managed ? ", with no initial focus move, focus containment or Escape handling" : "")}: keyboard users stay on the page behind it", p.File, Line(p, e), null, 1));
                }
            }
        }
    }

    // ---------------- AC5 ----------------
    private static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase) { "alert", "alertdialog", "application", "article", "banner", "button", "cell", "checkbox", "columnheader", "combobox", "complementary", "contentinfo", "definition", "dialog", "directory", "document", "feed", "figure", "form", "grid", "gridcell", "group", "heading", "img", "link", "list", "listbox", "listitem", "log", "main", "marquee", "math", "menu", "menubar", "menuitem", "menuitemcheckbox", "menuitemradio", "navigation", "none", "note", "option", "presentation", "progressbar", "radio", "radiogroup", "region", "row", "rowgroup", "rowheader", "scrollbar", "search", "searchbox", "separator", "slider", "spinbutton", "status", "switch", "tab", "table", "tablist", "tabpanel", "term", "textbox", "timer", "toolbar", "tooltip", "tree", "treegrid", "treeitem", "meter", "generic", "blockquote", "caption", "code", "deletion", "emphasis", "insertion", "paragraph", "strong", "subscript", "superscript", "time", "mark", "suggestion", "comment" };
    private static readonly HashSet<string> AbstractRoles = new(StringComparer.OrdinalIgnoreCase) { "command", "composite", "input", "landmark", "range", "roletype", "section", "sectionhead", "select", "structure", "widget", "window" };
    private static readonly HashSet<string> AriaAttrs = new(StringComparer.OrdinalIgnoreCase) { "aria-activedescendant", "aria-atomic", "aria-autocomplete", "aria-braillelabel", "aria-brailleroledescription", "aria-busy", "aria-checked", "aria-colcount", "aria-colindex", "aria-colindextext", "aria-colspan", "aria-controls", "aria-current", "aria-describedby", "aria-description", "aria-details", "aria-disabled", "aria-dropeffect", "aria-errormessage", "aria-expanded", "aria-flowto", "aria-grabbed", "aria-haspopup", "aria-hidden", "aria-invalid", "aria-keyshortcuts", "aria-label", "aria-labelledby", "aria-level", "aria-live", "aria-modal", "aria-multiline", "aria-multiselectable", "aria-orientation", "aria-owns", "aria-placeholder", "aria-posinset", "aria-pressed", "aria-readonly", "aria-relevant", "aria-required", "aria-roledescription", "aria-rowcount", "aria-rowindex", "aria-rowindextext", "aria-rowspan", "aria-selected", "aria-setsize", "aria-sort", "aria-valuemax", "aria-valuemin", "aria-valuenow", "aria-valuetext" };
    private static readonly Dictionary<string, string[]> RequiredStates = new(StringComparer.OrdinalIgnoreCase) { ["checkbox"] = new[] { "aria-checked" }, ["switch"] = new[] { "aria-checked" }, ["radio"] = new[] { "aria-checked" }, ["menuitemcheckbox"] = new[] { "aria-checked" }, ["menuitemradio"] = new[] { "aria-checked" }, ["slider"] = new[] { "aria-valuenow" }, ["scrollbar"] = new[] { "aria-controls", "aria-valuenow" }, ["combobox"] = new[] { "aria-expanded" }, ["heading"] = new[] { "aria-level" }, ["option"] = new[] { "aria-selected" }, ["tab"] = Array.Empty<string>(), ["meter"] = new[] { "aria-valuenow" } };
    private static readonly Dictionary<string, string[]> TokenValues = new(StringComparer.OrdinalIgnoreCase) { ["aria-hidden"] = new[] { "true", "false" }, ["aria-expanded"] = new[] { "true", "false", "undefined" }, ["aria-checked"] = new[] { "true", "false", "mixed", "undefined" }, ["aria-pressed"] = new[] { "true", "false", "mixed", "undefined" }, ["aria-selected"] = new[] { "true", "false", "undefined" }, ["aria-live"] = new[] { "off", "polite", "assertive" }, ["aria-haspopup"] = new[] { "true", "false", "menu", "listbox", "tree", "grid", "dialog" }, ["aria-current"] = new[] { "page", "step", "location", "date", "time", "true", "false" }, ["aria-sort"] = new[] { "ascending", "descending", "none", "other" }, ["aria-invalid"] = new[] { "true", "false", "grammar", "spelling" }, ["aria-autocomplete"] = new[] { "inline", "list", "both", "none" }, ["aria-orientation"] = new[] { "horizontal", "vertical", "undefined" }, ["aria-modal"] = new[] { "true", "false" }, ["aria-busy"] = new[] { "true", "false" }, ["aria-disabled"] = new[] { "true", "false" }, ["aria-required"] = new[] { "true", "false" }, ["aria-readonly"] = new[] { "true", "false" }, ["aria-multiline"] = new[] { "true", "false" }, ["aria-atomic"] = new[] { "true", "false" } };

    private static void Aria(ScanContext ctx, List<Page> pages)
    {
        foreach (var p in pages)
            foreach (var e in p.Doc.QuerySelectorAll("*"))
            {
                var role = e.GetAttribute("role");
                if (role is not null && !IsRazorExpression(role))
                {
                    foreach (var r in role.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (AbstractRoles.Contains(r)) ctx.Add(new Finding("invalid-aria-usage", "AC5", $"role=\"{r}\" is an abstract role and must not be used in content", p.File, Line(p, e), null, 1));
                        else if (!Roles.Contains(r) && !r.StartsWith("doc-")) ctx.Add(new Finding("invalid-aria-usage", "AC5", $"role=\"{r}\" is not an ARIA role", p.File, Line(p, e), null, 1));
                    }
                    if (RequiredStates.TryGetValue(role, out var req))
                        foreach (var attr in req) if (!e.HasAttribute(attr)) ctx.Add(new Finding("invalid-aria-usage", "AC5", $"role=\"{role}\" requires {attr}, which is missing: assistive technology cannot report its state", p.File, Line(p, e), null, 1));
                }
                foreach (var a in e.Attributes.Where(a => a.Name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!AriaAttrs.Contains(a.Name)) { ctx.Add(new Finding("invalid-aria-usage", "AC5", $"{a.Name} is not an ARIA attribute (misspelled?)", p.File, Line(p, e), null, 1)); continue; }
                    if (TokenValues.TryGetValue(a.Name, out var tokens) && !IsRazorExpression(a.Value) && !tokens.Contains(a.Value.Trim(), StringComparer.OrdinalIgnoreCase) && a.Value.Trim().Length > 0)
                        ctx.Add(new Finding("invalid-aria-usage", "AC5", $"{a.Name}=\"{a.Value}\" is not one of {string.Join("/", tokens)}", p.File, Line(p, e), null, 1));
                }
                if (e.GetAttribute("aria-hidden") == "true")
                {
                    var tag = e.TagName.ToLowerInvariant();
                    var focusableSelf = tag is "button" or "input" or "select" or "textarea" || tag == "a" && e.HasAttribute("href") || e.GetAttribute("tabindex") is { } t && t != "-1";
                    var focusableInside = e.QuerySelectorAll("button, input, select, textarea, a[href], [tabindex]").Any(c => c.GetAttribute("tabindex") != "-1" && !c.HasAttribute("disabled"));
                    if (focusableSelf || focusableInside) ctx.Add(new Finding("invalid-aria-usage", "AC5", $"aria-hidden=\"true\" on {(focusableSelf ? "a focusable element" : "an element that wraps focusable content")}: keyboard users reach what screen readers cannot see", p.File, Line(p, e), null, 1));
                }
            }
    }

    // ---------------- AC6 ----------------
    private static void Visual(ScanContext ctx, List<Page> pages, List<(string file, string text)> css)
    {
        // a site-wide reduced-motion guard only counts when it is universal: `* { animation: none / animation-duration: 0.01ms }` inside the media query
        var globalGuard = css.Where(c => !c.file.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase) && !c.file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && !c.file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .Any(c => Regex.IsMatch(c.text, @"prefers-reduced-motion:\s*reduce[^{]*\{[^{}]*\*[^{}]*\{[^}]*animation"));
        foreach (var (file, text) in css)
        {
            int LineOf(int index) => text.Take(index).Count(ch => ch == '\n') + 1 + (file.EndsWith(".razor") || file.EndsWith(".cshtml") ? StyleOffset(ctx.Repo.Text(file).Replace("@@", "@"), text) : 0);
            var rules = Regex.Matches(text, @"([^{}]+)\{([^{}]*)\}").Select(m => (selector: Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " "), body: m.Groups[2].Value, index: m.Index)).ToList();
            // focus outline removed without a replacement for the SAME selector (a global :focus-visible loses to a more specific :focus rule)
            foreach (var r in rules)
            {
                if (!Regex.IsMatch(r.body, @"outline\s*:\s*(none|0)\b|outline-width\s*:\s*0|outline-style\s*:\s*none")) continue;
                if (Regex.IsMatch(r.selector, @"^h[1-6]\b.*:focus|\[tabindex=""?-1""?\]")) continue;                                   // a programmatic focus target
                if (Regex.IsMatch(r.selector, @":focus:not\(:focus-visible\)")) continue;                                             // mouse focus only; keyboard focus keeps its ring
                var visibleHere = Regex.IsMatch(r.body, @"box-shadow\s*:(?!\s*none)|border(-color)?\s*:|background(-color)?\s*:") && r.selector.Contains(":focus");
                var baseSelector = Regex.Replace(r.selector, @":focus(-visible|-within)?", "").Trim();
                var replacement = rules.Any(o => o.index != r.index && Regex.IsMatch(o.selector, $@"^{Regex.Escape(baseSelector)}\s*:focus(-visible)?$") && Regex.IsMatch(o.body, @"outline\s*:(?!\s*(none|0)\b)|box-shadow\s*:(?!\s*none)|border(-color)?\s*:"));
                if (!visibleHere && !replacement) ctx.Add(new Finding("focus-outline-removed", "AC6", $"`{Trunc(r.selector, 40)}` removes the focus outline and no :focus/:focus-visible rule for it draws a replacement: keyboard focus becomes invisible", file, LineOf(r.index), null, 1));
            }
            // motion: an animation in a file that never mentions prefers-reduced-motion, with no universal site-wide guard
            var motion = Regex.Match(text, @"@keyframes\s+[\w-]+|(?<![\w-])animation(-name)?\s*:\s*(?!none)[^;{}]+;");
            if (motion.Success && !text.Contains("prefers-reduced-motion") && !globalGuard)
                ctx.Add(new Finding("motion-without-reduced-motion", "AC6", $"`{Trunc(motion.Value.Trim(), 40)}` runs with no prefers-reduced-motion guard in this stylesheet and no universal guard in the site's CSS", file, LineOf(motion.Index), null, 1));
            // literal colour pairs: foreground on background within one rule
            foreach (var r in rules)
            {
                if (Regex.IsMatch(r.selector, @":disabled|\[disabled\]|\.disabled|::placeholder|:placeholder")) continue;   // inactive controls are exempt
                var fg = Regex.Match(r.body, @"(?<![\w-])color\s*:\s*([^;]+);"); var bg = Regex.Match(r.body, @"background(-color)?\s*:\s*([^;]+);");
                if (!fg.Success || !bg.Success) continue;
                var f = ParseColor(fg.Groups[1].Value.Trim()); var b = ParseColor(bg.Groups[2].Value.Trim());
                if (f is null || b is null || b.Value.a < 1) continue;
                var ratio = Contrast(f.Value, b.Value);
                var large = Regex.Match(r.body, @"font-size\s*:\s*([\d.]+)(px|rem|em)") is { Success: true } fs && ParsePx(fs.Groups[1].Value, fs.Groups[2].Value) is { } px && (px >= 24 || px >= 18.66 && Regex.IsMatch(r.body, @"font-weight\s*:\s*(bold|[6-9]00)"));
                var min = large ? 3.0 : 4.5;
                if (ratio < min) ctx.Add(new Finding("visual-and-motion-safety", "AC6", $"`{Trunc(r.selector, 40)}`: {fg.Groups[1].Value.Trim()} on {bg.Groups[2].Value.Trim()} is {ratio:F1}:1, below the {min}:1 minimum for {(large ? "large" : "normal")} text", file, LineOf(r.index), null, 1));
            }
        }
        foreach (var p in pages)
            foreach (var e in p.Doc.QuerySelectorAll("[style]"))
            {
                var style = e.GetAttribute("style") ?? "";
                var fg = Regex.Match(style, @"(?<![\w-])color\s*:\s*([^;]+)"); var bg = Regex.Match(style, @"background(-color)?\s*:\s*([^;]+)");
                if (!fg.Success || !bg.Success) continue;
                var f = ParseColor(fg.Groups[1].Value.Trim()); var b = ParseColor(bg.Groups[2].Value.Trim());
                if (f is null || b is null) continue;
                var ratio = Contrast(f.Value, b.Value);
                if (ratio < 4.5) ctx.Add(new Finding("visual-and-motion-safety", "AC6", $"inline style: {fg.Groups[1].Value.Trim()} on {bg.Groups[2].Value.Trim()} is {ratio:F1}:1", p.File, Line(p, e), null, 1));
            }
    }

    private static int StyleOffset(string fileText, string styleBody) { var i = fileText.IndexOf(styleBody, StringComparison.Ordinal); return i < 0 ? 0 : fileText.Take(i).Count(c => c == '\n'); }
    private static double? ParsePx(string v, string unit) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? unit == "px" ? d : d * 16 : null;

    private static readonly Dictionary<string, (double r, double g, double b)> Named = new(StringComparer.OrdinalIgnoreCase) { ["white"] = (255, 255, 255), ["black"] = (0, 0, 0), ["red"] = (255, 0, 0), ["gray"] = (128, 128, 128), ["grey"] = (128, 128, 128), ["silver"] = (192, 192, 192), ["lightgray"] = (211, 211, 211), ["lightgrey"] = (211, 211, 211), ["darkgray"] = (169, 169, 169), ["dimgray"] = (105, 105, 105), ["blue"] = (0, 0, 255), ["navy"] = (0, 0, 128), ["green"] = (0, 128, 0), ["yellow"] = (255, 255, 0), ["orange"] = (255, 165, 0), ["whitesmoke"] = (245, 245, 245), ["gainsboro"] = (220, 220, 220) };

    public static (double r, double g, double b, double a)? ParseColor(string v)
    {
        v = v.Trim().TrimEnd('!').Trim();
        if (v.Contains("var(") || v.Contains("transparent") || v.Contains("inherit") || v.Contains("currentColor", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = Regex.Match(v, @"^#([0-9a-fA-F]{3,8})\b");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length is 6 or 8) { var r = Convert.ToInt32(h[..2], 16); var g = Convert.ToInt32(h[2..4], 16); var b = Convert.ToInt32(h[4..6], 16); var a = h.Length == 8 ? Convert.ToInt32(h[6..8], 16) / 255.0 : 1; return (r, g, b, a); }
            return null;
        }
        var rgb = Regex.Match(v, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+))?\s*\)");
        if (rgb.Success) return (int.Parse(rgb.Groups[1].Value), int.Parse(rgb.Groups[2].Value), int.Parse(rgb.Groups[3].Value), rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) : 1);
        var hsl = Regex.Match(v, @"^hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%");
        if (hsl.Success) { var (r, g, b) = HslToRgb(double.Parse(hsl.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(hsl.Groups[2].Value, CultureInfo.InvariantCulture) / 100, double.Parse(hsl.Groups[3].Value, CultureInfo.InvariantCulture) / 100); return (r, g, b, 1); }
        if (Named.TryGetValue(v.Split(' ')[0], out var n)) return (n.r, n.g, n.b, 1);
        return null;
    }
    private static (double, double, double) HslToRgb(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s, X = C * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - C / 2;
        var (r, g, b) = h < 60 ? (C, X, 0.0) : h < 120 ? (X, C, 0.0) : h < 180 ? (0.0, C, X) : h < 240 ? (0.0, X, C) : h < 300 ? (X, 0.0, C) : (C, 0.0, X);
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }
    public static double Contrast((double r, double g, double b, double a) f, (double r, double g, double b, double a) b)
    {
        double Lum((double r, double g, double b, double a) c) { double Ch(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); } return 0.2126 * Ch(c.r) + 0.7152 * Ch(c.g) + 0.0722 * Ch(c.b); }
        var l1 = Lum(f); var l2 = Lum(b); var hi = Math.Max(l1, l2); var lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    // ---------------- AC7 ----------------
    private static void Enforcement(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var testText = string.Join("\n", ctx.Workspace.Tests.SelectMany(t => t.Trees).Select(t => t.GetRoot().ToString()));
        var packages = repo.Projects.SelectMany(p => p.Packages.Select(x => x.id)).ToList();
        var ci = string.Join("\n", Readiness.WorkflowFiles(repo).Select(repo.Text));
        var pkgJson = string.Join("\n", repo.FilesNamed("package.json").Select(repo.Text));
        var lintRules = pkgJson.Contains("eslint-plugin-jsx-a11y") || pkgJson.Contains("eslint-plugin-vuejs-accessibility") || repo.FilesNamed(".htmlhintrc", ".pa11yci", "pa11yci.json", "axe.config.js").Any() || packages.Any(p => Regex.IsMatch(p, @"(?i)accessibility|a11y") && !p.Contains("AxeCore"));
        var axeTests = Regex.IsMatch(testText, @"AxeCore|RunAxe|axe\.|Accessibility(Checker|Assert|Scan)|CheckAccessibility|Pa11y|Lighthouse") || packages.Any(p => Regex.IsMatch(p, @"(?i)Deque\.AxeCore|Axe|Pa11y|Playwright\.Axe|Lighthouse"));
        var ciGate = Regex.IsMatch(ci, @"(?i)axe|pa11y|lighthouse|a11y|accessibility");
        var documented = Readiness.DocumentationFiles(repo).Any(f => Regex.IsMatch(repo.Text(f), @"(?i)\bWCAG\b|accessibility statement|\ba11y\b"));
        var rung = ciGate && axeTests ? "Prevented" : axeTests ? "Verified" : documented || lintRules ? "Documented" : "none";
        if (rung == "none") ctx.Add(new Finding("accessibility-checks-in-ci", "AC7", "nothing enforces accessibility: no accessibility assertion in the component tests, no browser-based accessibility check in the pipeline, no a11y lint rules", null, null, null, 2));
        ctx.Measure("AC7", rung switch { "Prevented" => 10, "Verified" => 7, "Documented" => 4, _ => 0 }, note: $"ladder rung: {rung} (lint {lintRules}, tests {axeTests}, CI {ciGate}, documented {documented})");
    }

    // ---------------- Razor markup XSS (D29) ----------------
    private static void RazorXss(ScanContext ctx, List<Page> pages)
    {
        foreach (var p in pages.Where(p => p.File.EndsWith(".razor") || p.File.EndsWith(".cshtml")))
            for (var i = 0; i < p.Lines.Length; i++)
            {
                var l = p.Lines[i];
                var m = Regex.Match(l, @"\(MarkupString\)\s*\(?\s*([^)]+?)\s*\)?\s*\)|new MarkupString\(\s*([^)]+)\s*\)|@Html\.Raw\(\s*([^)]+)\s*\)");
                if (!m.Success) continue;
                var expr = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (Regex.IsMatch(expr, @"(?i)saniti[sz]|encode|escape|Localizer|Resource|Markdown\.ToHtml|\bconst\b|^""")) continue;
                var fileText = string.Join("\n", p.Lines) + (ctx.Repo.Exists(p.File + ".cs") ? ctx.Repo.Text(p.File + ".cs") : "");
                if (Regex.IsMatch(expr, @"^[A-Z]\w*$") && Regex.IsMatch(fileText, $@"(?i)(const|static readonly)\s+string\s+{Regex.Escape(expr)}\b")) continue;
                if (Regex.IsMatch(expr, @"^[\w.]+$") && Regex.IsMatch(fileText, $@"{Regex.Escape(expr.Split('.')[^1])}\s*=\s*[^;]*(?i:saniti[sz]|encode|AntiXss|Clean\()")) continue;   // assigned from a sanitiser
                ctx.Add(new Finding("cross-site-scripting", "D29", $"{Trunc(m.Value, 60)} renders `{Trunc(expr, 30)}` as raw HTML with no sanitiser in sight: stored or reflected markup executes in the page", p.File, i + 1, i + 1, 2));
            }
    }

    private static string Trunc(string? s, int n) { s ??= ""; s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length <= n ? s : s[..n] + "…"; }
}
