using Cai.Reference.Engine;

namespace Cai.Reference.Tests;

public class AccessibilityDetectorTests
{
    private static ScanContext Scan(params (string file, string markup)[] files)
    {
        var fx = new Fixture().Project("Shop.Web", web: true, sources: ("Program.cs", "var b = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args); b.Build().Run();"));
        foreach (var (file, markup) in files) fx.Add("src/Shop.Web/" + file, markup);
        return fx.Run(Accessibility.Run);
    }

    [Fact]
    public void Images_without_alt_fire_and_decorative_or_labelled_ones_stay_silent()
    {
        var ctx = Scan(("Components/Pages/Home.razor", """
            @page "/"
            <img src="a.png" />
            <img src="b.png" alt="" />
            <img src="c.png" alt="The entrance" />
            <img src="d.png" aria-hidden="true" />
            """));
        ctx.Fires("missing-text-alternative", 1);
    }

    [Fact]
    public void Razor_Pages_tag_helpers_count_as_labels_and_links()
    {
        var ctx = Scan(("Pages/Admin/Edit.cshtml", """
            @page
            @model EditModel
            <form method="post">
              <label asp-for="Input.Name">Room name</label>
              <input asp-for="Input.Name" />
              <a asp-page="/Admin/Index">Back</a>
              <input id="loose" type="text" placeholder="Search" />
            </form>
            """));
        ctx.Fires("form-control-without-label", 1);
        Assert.Contains("placeholder", ctx.Rule("form-control-without-label").Single().Message);
        ctx.Silent("non-keyboard-accessible-interaction");
    }

    [Fact]
    public void Razor_escaped_at_keyframes_in_an_inline_style_block_is_motion_without_a_guard()
    {
        var ctx = Scan(
            ("Components/Shared/Skeleton.razor", """
                <style>
                    .line { animation: shimmer 1.2s linear infinite; }
                    @@keyframes shimmer { from { opacity: 0; } to { opacity: 1; } }
                </style>
                <div class="line"></div>
                """),
            ("Components/Shared/Saved.razor", """
                <style>
                    @@media (prefers-reduced-motion: no-preference) { .saved { animation: pop 200ms; } }
                    @@keyframes pop { from { transform: scale(0.9); } to { transform: scale(1); } }
                </style>
                <span class="saved">Saved</span>
                """));
        ctx.Fires("motion-without-reduced-motion", 1);
        Assert.Contains("Skeleton.razor", ctx.Rule("motion-without-reduced-motion").Single().File);
    }

    [Fact]
    public void Outline_none_fires_unless_the_same_selector_draws_a_focus_replacement()
    {
        var ctx = Scan(
            ("Components/Layout/Nav.razor.css", ".nav a:focus { outline: none; }\n.nav a { color: #000; }"),
            ("Components/Layout/Good.razor.css", ".btn:focus { outline: none; }\n.btn:focus-visible { outline: 2px solid #0b5394; }\n.btn:focus:not(:focus-visible) { outline: none; }"),
            ("Components/Pages/Home.razor", "<nav class=\"nav\"><a href=\"/\">Home</a></nav>"));
        ctx.Fires("focus-outline-removed", 1);
        Assert.Contains("Nav.razor.css", ctx.Rule("focus-outline-removed").Single().File);
    }

    [Fact]
    public void MarkupString_fed_from_a_sanitiser_is_not_cross_site_scripting()
    {
        var ctx = Scan(
            ("Components/Shared/Notice.razor", """
                @inject NoticeSanitizer Sanitizer
                <aside>@((MarkupString)_safeHtml)</aside>
                @code { private string _safeHtml = ""; protected override void OnInitialized() { _safeHtml = Sanitizer.Sanitize(Html); } [Parameter] public string Html { get; set; } = ""; }
                """),
            ("Components/Shared/Raw.razor", """
                <div>@((MarkupString)Body)</div>
                @code { [Parameter] public string Body { get; set; } = ""; }
                """));
        ctx.Fires("cross-site-scripting", 1);
        Assert.Contains("Raw.razor", ctx.Rule("cross-site-scripting").Single().File);
    }

    [Fact]
    public void An_error_message_is_associated_when_the_field_names_it_even_through_a_razor_expression()
    {
        var ctx = Scan(("Components/Pages/Book.razor", """
            @page "/book"
            <label for="email">E-mail</label>
            <input id="email" aria-describedby="@ErrorId()" />
            <p id="email-error" class="field-error">Required</p>
            <label for="n">Attendees</label>
            <input id="n" type="number" />
            <p class="field-error">At least one</p>
            @code { private string ErrorId() => "email-error"; }
            """));
        ctx.Fires("form-error-not-associated", 1);
        Assert.Equal(7, ctx.Rule("form-error-not-associated").Single().Line);
    }

    [Fact]
    public void Repositories_without_markup_skip_the_lens()
    {
        var ctx = new Fixture().Project("Lib", sources: ("A.cs", "public class A { }")).Run(Accessibility.Run);
        Assert.Contains(ctx.NotMeasured, n => n.Id == "AC1" && n.Reason.Contains("not applicable"));
        Assert.DoesNotContain(ctx.Measurements, m => m.Id.StartsWith("AC"));
    }
}
