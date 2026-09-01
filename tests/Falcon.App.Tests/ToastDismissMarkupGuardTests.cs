using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 13 C2 (backlog item 13) — the error toast's DISMISS control, pinned
/// against the markup.
///
/// <para><b>Why this file exists at all.</b> The behaviour half of the dismiss
/// work is real VM state and is tested as behaviour
/// (<c>RadioSessionViewModelTests</c>: the command clears the text and the
/// count, and winds the rate-limit clock back so the next error is not
/// swallowed). None of that reaches the operator unless a control is actually
/// BOUND to it — a command nothing invokes is a private method with extra
/// steps. This is the same gap <c>ConnectionFlowSourceGuardTests</c> closes for
/// the About page's constants: the constants are pinned there, and the fact
/// that the PAGE renders them is pinned separately, because those are two
/// different ways to be wrong.</para>
///
/// <para><b>Why a new file rather than the obvious one.</b> OperatePage.xaml's
/// existing guard home is <c>OperateStyleAdoptionGuardTests</c>, which round
/// 13's A2 phase owns and is editing in a parallel stream; the plan's ownership
/// map (§6) forbids two streams sharing a file. This file therefore holds C2's
/// OperatePage pins only, and should be folded into the style-adoption guard by
/// whoever next has both in hand.</para>
///
/// <para><b>ACCEPTED LIMITATION</b>, the house one: this reads the FILE, not a
/// rendered page. A value supplied by a style, a converter or code-behind is
/// invisible here, and nothing below proves a press does anything on a device.
/// What it catches is the accident — a deleted button, a binding that lost its
/// command, a control that stopped being conditional and now floats beside an
/// empty status line.</para>
/// </summary>
public class ToastDismissMarkupGuardTests
{
    private static readonly string OperatePage =
        Path.Combine("src", "Falcon.App", "Views", "OperatePage.xaml");

    [Fact]
    public void TheStatusLine_PairsTheCaptionWithADismissButton_InThatOrder()
    {
        var (caption, dismiss, row) = StatusRow();

        // The caption keeps the ELASTIC column and the truncation; the button
        // takes a fixed one at the right edge. That is what makes a long
        // message truncate instead of pushing the control off the screen —
        // reverse the two and the bug comes back at the first long error.
        Assert.Equal("*,Auto", Property(row, "ColumnDefinitions"));
        Assert.Null(Property(caption, "Grid.Column"));          // implicit 0
        Assert.Equal("1", Property(dismiss, "Grid.Column"));
        Assert.Equal("TailTruncation", Property(caption, "LineBreakMode"));

        // The caption still shows the toast, and still in the error style —
        // this row was a bare Label before C2 and the rework must not have
        // quietly changed what it displays.
        Assert.Equal("{Binding Session.ToastText}", Property(caption, "Text"));
        Assert.Equal("{StaticResource ErrorCaption}", Property(caption, "Style"));
    }

    [Fact]
    public void TheDismissButton_IsWired_Conditional_Reachable_AndNamed()
    {
        var (_, dismiss, _) = StatusRow();

        // WIRED: the command the VM exposes, by its generated name.
        Assert.Equal("{Binding Session.DismissToastCommand}", Property(dismiss, "Command"));

        // CONDITIONAL: no ✕ floating beside an empty status line.
        Assert.Equal("{Binding Session.HasToast}", Property(dismiss, "IsVisible"));

        // REACHABLE: the app's 44 dp touch-target class, both dimensions.
        Assert.Equal("44", Property(dismiss, "WidthRequest"));
        Assert.Equal("44", Property(dismiss, "HeightRequest"));

        // NAMED: a bare glyph has no accessible name of its own, so the
        // description is the only thing a screen reader can announce.
        Assert.False(string.IsNullOrWhiteSpace(Description(dismiss)),
            "the dismiss button is a bare glyph and MUST carry a semantic description");

        // …and it reads as an error control, like the caption beside it.
        Assert.Contains("ErrorText", Property(dismiss, "TextColor") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void TheDismissGlyph_IsNotAChevron_SoTheChevronGeometryGuardDoesNotOwnIt()
    {
        // Stated rather than assumed (the plan asked for the check):
        // ChevronGeometryGuardTests scans a CLOSED glyph set and requires every
        // member to be 44 dp wide. ✕ is deliberately outside that set — this
        // button is not a wheel — so the two guards do not overlap and the 44
        // above is C2's own requirement, held HERE. If a later edit changed the
        // glyph to one of those, ownership would move without anyone noticing.
        var glyph = Property(StatusRow().Dismiss, "Text");

        Assert.Equal("✕", glyph);
        Assert.DoesNotContain(glyph, new[] { "▲", "▼", "◀", "▶", "−", "+" });
    }

    // ---- element selection ---------------------------------------------------

    /// <summary>The status row: the toast caption and its dismiss button.
    /// Found by what they ARE (the toast binding; the command binding) rather
    /// than by position, so the pins follow the controls if the page moves —
    /// and FAIL rather than silently find nothing if either disappears.
    /// </summary>
    private static (XElement Caption, XElement Dismiss, XElement Row) StatusRow()
    {
        var page = Load(OperatePage);

        var caption = page.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && (Property(e, "Text") ?? "").Contains("Session.ToastText", StringComparison.Ordinal));

        var dismiss = page.Descendants().Single(e =>
            e.Name.LocalName == "Button"
            && (Property(e, "Command") ?? "").Contains("DismissToastCommand", StringComparison.Ordinal));

        // They are siblings in one Grid — the layout claim the first test makes
        // only means anything if they share a parent.
        var row = caption.Parent!;
        Assert.Equal("Grid", row.Name.LocalName);
        Assert.Same(row, dismiss.Parent);

        return (caption, dismiss, row);
    }

    // ---- readers (house shape: an attribute OR a property element) -----------

    private static string? Property(XElement e, string property)
        => e.Attribute(property)?.Value
           ?? e.Elements()
               .FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + property)?.Value;

    private static string? Description(XElement e)
        => e.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "SemanticProperties.Description")?.Value;

    [Fact]
    public void TheMarkupReader_SeesBothSpellings_AndReportsUnsetAsNull()
    {
        // Anti-vacuity for every Property() call above: a reader that could only
        // see attributes would report "unset" for a property element and turn
        // an assertion into a false alarm — or, with Assert.Null, into a pass.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Button IsVisible="{Binding HasToast}" />
              <Button><Button.IsVisible>{Binding HasToast}</Button.IsVisible></Button>
              <Button />
            </ContentView>
            """);

        var buttons = markup.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal("{Binding HasToast}", Property(buttons[0], "IsVisible"));
        Assert.Equal("{Binding HasToast}", Property(buttons[1], "IsVisible"));
        Assert.Null(Property(buttons[2], "IsVisible"));
    }

    private static XDocument Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }
}
