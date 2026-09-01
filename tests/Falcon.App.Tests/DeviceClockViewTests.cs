using System.Text;
using System.Text.RegularExpressions;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 4, Contract K2 — the two facts that make the shared clock
/// card work, pinned against the source because the wiring lives in MAUI-typed
/// code this net10.0 host project cannot reference (Falcon.App targets
/// android/windows only):
///
///   1. the card subscribes its Loaded event to the VM's once-per-session
///      EnsureLoaded — the ONLY trigger that fires when the DI-singleton VM is
///      first constructed by the HOP settings pane AFTER the session is
///      already Ready (see DeviceSettingsViewModelTests
///      .VmConstructedWhileAlreadyReady_LoadsNothingUntilSomethingCalls
///      EnsureLoaded for the failure it prevents: a clock stuck at "—" for the
///      whole session);
///   2. the card BINDS the resolved DeviceSettingsViewModel — not whatever VM
///      its host happens to carry — which is what makes the two placements the
///      same clock.
///
/// A source scan is only as good as what it refuses to be fooled by. Round-2
/// audit defeated the first version twice — once by COMMENTING OUT the call
/// inside the lambda, once by assigning `BindingContext = new object()` while
/// the type name still appeared elsewhere in the file. So this version strips
/// comments AND string literals before matching, extracts the Loaded
/// subscription's actual statement, and matches the BindingContext ASSIGNMENT
/// against the identifier that the service-provider resolution assigned. The
/// stripper is itself pinned below.
///
/// Deliberate rigidity: refactoring to a named handler (`Loaded += OnLoaded;`)
/// fails this pin even though it would behave correctly. That is the intended
/// cost — the wiring is load-bearing and invisible to every behavioural test,
/// so changing its shape should be a conscious act that updates this guard.
///
/// <para><b>ACCEPTED LIMITATION (owner deferral, 2026-08-12).</b> The scan
/// strips comments and literals but does not evaluate preprocessor regions:
/// wiring wrapped in <c>#if false</c> still satisfies both pins while the
/// compiler excludes it (round-3 audit). Accident classes are caught;
/// preprocessor-hidden removal is adversarial construction, backstopped by
/// review/audit and by bench item A4b (the HOP-first clock population).</para>
/// </summary>
public class DeviceClockViewTests
{
    private const string CodeBehindRelativePath =
        @"src\Falcon.App\Views\SettingsParts\DeviceClockView.xaml.cs";

    [Fact]
    public void DeviceClockView_SubscribesLoadedToEnsureLoaded()
    {
        var source = ExecutableSource();
        var vm = ResolvedViewModelIdentifier(source);

        int plusEquals = FindLoadedSubscription(source);
        var statement = StatementAt(source, plusEquals);

        // The CALL must be in the subscription's own body — not merely present
        // somewhere in the file, and (comments now stripped) not commented out.
        Assert.Matches(
            new Regex(@"\b" + Regex.Escape(vm) + @"\s*\.\s*EnsureLoaded\s*\(\s*\)"),
            statement);
    }

    [Fact]
    public void DeviceClockView_BindsTheResolvedViewModel()
    {
        var source = ExecutableSource();
        var vm = ResolvedViewModelIdentifier(source);

        // The ASSIGNMENT, not two independent substrings: BindingContext must
        // take the value that the service-provider resolution produced.
        Assert.Matches(
            new Regex(@"BindingContext\s*=(?![=>])\s*" + Regex.Escape(vm) + @"\s*;"),
            source);
    }

    [Fact]
    public void TheScanner_DoesNotSeeCommentedOutOrQuotedCode()
    {
        // The guard's own guard: if this ever regresses, both pins above go
        // quietly blind, which is exactly how the round-2 evasions worked.
        var stripped = StripCommentsAndStrings("""
            a(); // EnsureLoaded();
            /* BindingContext = _device; */
            var s = "EnsureLoaded();";
            b();
            """);

        Assert.DoesNotContain("EnsureLoaded", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingContext", stripped, StringComparison.Ordinal);
        Assert.Contains("a()", stripped, StringComparison.Ordinal);
        Assert.Contains("b()", stripped, StringComparison.Ordinal);
    }

    // ---- the scanned source -------------------------------------------------

    private static string ExecutableSource()
    {
        var path = Path.Combine(FindRepoRoot(), CodeBehindRelativePath);
        Assert.True(File.Exists(path), "the DeviceClockView code-behind is gone from " + CodeBehindRelativePath);
        return StripCommentsAndStrings(File.ReadAllText(path));
    }

    /// <summary>The identifier the code-behind assigned from the service
    /// provider — the ONLY thing downstream assertions will accept as "the
    /// view model".</summary>
    private static string ResolvedViewModelIdentifier(string source)
    {
        var match = Regex.Match(source,
            @"(?<local>[A-Za-z_]\w*)\s*=(?![=>])[^;]*?Get(?:Required)?Service\s*"
            + @"(?:<\s*DeviceSettingsViewModel\s*>|\(\s*typeof\s*\(\s*DeviceSettingsViewModel\s*\)\s*\))");

        Assert.True(match.Success,
            "the code-behind no longer resolves a DeviceSettingsViewModel from the service provider — "
            + "K2 requires the card to bind the DI singleton itself, not an inherited context");
        return match.Groups["local"].Value;
    }

    private static int FindLoadedSubscription(string source)
    {
        var match = Regex.Match(source, @"\bLoaded\s*\+=");
        Assert.True(match.Success,
            "the DeviceClockView no longer subscribes to Loaded — the HOP-settings placement would "
            + "render — for the whole session when the VM is first constructed after Ready");
        return match.Index + match.Length;
    }

    /// <summary>The rest of the statement starting at <paramref name="start"/>:
    /// up to the first semicolon at nesting depth zero, so both the expression
    /// lambda and the braced-block lambda are covered.</summary>
    private static string StatementAt(string source, int start)
    {
        int depth = 0;
        for (int i = start; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(' or '{' or '[': depth++; break;
                case ')' or '}' or ']': depth--; break;
                case ';' when depth == 0: return source[start..i];
            }
        }
        return source[start..];
    }

    /// <summary>C# source with comments and literal contents removed, so a
    /// commented-out or quoted occurrence can never satisfy a pin. Structure is
    /// preserved (each removal leaves a separator) — this is a scanner, not a
    /// parser.</summary>
    private static string StripCommentsAndStrings(string src)
    {
        var output = new StringBuilder(src.Length);
        int i = 0;

        while (i < src.Length)
        {
            char c = src[i];
            char next = i + 1 < src.Length ? src[i + 1] : '\0';

            if (c == '/' && next == '/')                       // line comment
            {
                while (i < src.Length && src[i] != '\n') i++;
                output.Append('\n');
                continue;
            }

            if (c == '/' && next == '*')                       // block comment
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i = Math.Min(i + 2, src.Length);
                output.Append(' ');
                continue;
            }

            if (c == '@' && next == '"')                       // verbatim string
            {
                i += 2;
                while (i < src.Length)
                {
                    if (src[i] != '"') { i++; continue; }
                    if (i + 1 < src.Length && src[i + 1] == '"') { i += 2; continue; }
                    i++;
                    break;
                }
                output.Append("\"\"");
                continue;
            }

            if (c is '"' or '\'')                              // string / char literal
            {
                char quote = c;
                i++;
                while (i < src.Length)
                {
                    if (src[i] == '\\') { i += 2; continue; }
                    if (src[i] == quote) { i++; break; }
                    i++;
                }
                output.Append(quote).Append(quote);
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
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
