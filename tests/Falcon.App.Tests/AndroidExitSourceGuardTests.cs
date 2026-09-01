using System.Text.RegularExpressions;

namespace Falcon.App.Tests;

/// <summary>
/// CLONE ROUND 12 §6 F5 — the Android notification's <b>Exit</b> action,
/// pinned as SOURCE.
///
/// <para><b>Why source is the only option here.</b> RadioForegroundService is
/// an Android <c>Service</c>: it compiles only for the android TFM, needs a
/// device to run, and its whole contract is about Android's own lifecycle
/// callbacks. There is no host in this suite that can start it. What CAN be
/// held is the mechanism the plan specified exactly — because every clause of
/// it is a decision with a failure mode, and every one is a single line that a
/// later edit could take away silently:</para>
/// <list type="bullet">
///   <item><b>GetService</b>, not GetActivity/GetBroadcast: the work is the
///     service's own teardown. An activity intent would race the UI; a
///     broadcast needs a receiver that does not exist.</item>
///   <item><b>A UNIQUE, fully-qualified action</b> and its own request code:
///     the action decides whether the app dies, and a bare word collides.
///     The request code keeps <c>UpdateCurrent</c> from folding the Exit
///     intent and the content intent into one.</item>
///   <item><b>Immutable</b>: nothing outside this app rewrites an intent that
///     kills it.</item>
///   <item><b>The branch runs BEFORE StartForeground</b>: the command path
///     promotes the service unconditionally, and promoting it on the way out
///     re-posts the notification the operator just used to quit.</item>
///   <item><b>ONE teardown helper, TWO call sites</b>: the recents swipe and
///     the Exit action mean the same thing, and two copies of a
///     close→drop→stop→kill sequence drift.</item>
///   <item><b>NotSticky</b> is retained on both paths: a sticky restart after
///     a kill resurrects an orphan notification with no link behind it.</item>
/// </list>
///
/// <para><b>THE RUNTIME CHECK IS A MANUAL ONE</b> and is recorded as such in
/// docs/ui.md: tap Exit in the tray with a live link and the app is gone.
/// Nothing below claims to have done that.</para>
///
/// <para>ACCEPTED LIMITATION, as everywhere in this house style: the reader
/// removes comments but does not evaluate preprocessor regions or follow
/// indirection. Accidents (a deleted line, a commented-out one, a reordered
/// branch) are caught; adversarial construction is backstopped by review.</para>
/// </summary>
public class AndroidExitSourceGuardTests
{
    private static readonly string ServiceFile = Path.Combine(
        "src", "Falcon.App", "Platforms", "Android", "RadioForegroundService.cs");

    [Fact]
    public void TheExitAction_IsUniqueAndFullyQualified_WithItsOwnRequestCode()
    {
        var code = Source();

        var action = Regex.Match(code, @"ExitAction\s*=\s*""(?<value>[^""]*)""");
        Assert.True(action.Success, "no ExitAction constant declared");
        var value = action.Groups["value"].Value;

        // Fully qualified under this app's own id — an Intent action is a
        // process-wide string, and this one decides whether the app dies.
        Assert.StartsWith("com.falconrc.app", value, StringComparison.Ordinal);
        Assert.Contains("EXIT", value, StringComparison.Ordinal);

        // Its own request code, distinct from the content intent's 0.
        var request = Regex.Match(code, @"ExitRequestCode\s*=\s*(?<value>\d+)\s*;");
        Assert.True(request.Success, "no ExitRequestCode constant declared");
        Assert.NotEqual("0", request.Groups["value"].Value);
    }

    [Fact]
    public void TheExitPendingIntent_IsGetService_AndImmutable()
    {
        var code = Source();

        var call = Regex.Match(code,
            @"PendingIntent\.GetService\(\s*this,\s*ExitRequestCode,\s*(?<intent>\w+),\s*(?<flags>[^)]*)\)");
        Assert.True(call.Success,
            "the Exit action must use PendingIntent.GetService with ExitRequestCode — the work is "
            + "this service's own teardown, so it needs no activity and no receiver");
        Assert.Contains("Immutable", call.Groups["flags"].Value, StringComparison.Ordinal);

        // …and the intent it carries really targets this service with the
        // action the branch tests for.
        Assert.Contains("new Intent(this, typeof(RadioForegroundService))", code, StringComparison.Ordinal);
        Assert.Contains(".SetAction(ExitAction)", code, StringComparison.Ordinal);

        // The notification actually offers it.
        Assert.Contains("AddAction(0, ExitActionLabel, ", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitBranch_RunsBeforeStartForeground()
    {
        // The ORDER is the whole point of the branch. Reversed, the service
        // promotes itself — re-posting the notification — and only then tears
        // down, which is the state the operator was trying to leave.
        var command = OnStartCommandBody();

        int branch = command.IndexOf("intent?.Action == ExitAction", StringComparison.Ordinal);
        int promote = command.IndexOf("StartForeground(", StringComparison.Ordinal);

        Assert.True(branch >= 0, "OnStartCommand does not branch on the Exit action");
        Assert.True(promote >= 0, "OnStartCommand no longer calls StartForeground at all");
        Assert.True(branch < promote,
            "the Exit branch must come BEFORE StartForeground — the command path promotes the "
            + "service unconditionally, and the exit path must not");

        // NotSticky on the exit path as well as the ordinary one.
        Assert.Contains("StartCommandResult.NotSticky", command, StringComparison.Ordinal);
        Assert.DoesNotContain("StartCommandResult.Sticky", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeardown_IsOneSharedHelper_CalledFromBothGestures()
    {
        var code = Source();

        // One declaration…
        Assert.Single(Regex.Matches(code, @"private void TearDownAndExit\(\)"));

        // …and exactly two call sites: the recents swipe and the Exit branch.
        Assert.Equal(2, Regex.Matches(code, @"TearDownAndExit\(\);").Count);
        Assert.Contains("OnTaskRemoved", code, StringComparison.Ordinal);

        // The sequence itself, in order — it is the part that was proven on a
        // device (the MAUI/Glide relaunch crash class) and must not be
        // reordered by a later tidy-up.
        int close = code.IndexOf("session.Close()", StringComparison.Ordinal);
        int drop = code.IndexOf("StopForeground(StopForegroundFlags.Remove)", StringComparison.Ordinal);
        int stop = code.IndexOf("StopSelf();", StringComparison.Ordinal);
        int kill = code.IndexOf("KillProcess(", StringComparison.Ordinal);

        Assert.True(close >= 0 && drop > close && stop > drop && kill > stop,
            "the shared teardown must keep the close → drop notification → stop → delayed kill order");
        Assert.Contains("PostDelayed(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuard_CanMiss()
    {
        // Anti-vacuity for every "the source contains this" above: the reader
        // strips comments, so a commented-out mechanism reads as a deleted
        // one — and it does not simply match everything.
        var code = Source();

        Assert.DoesNotContain("PendingIntent.GetBroadcast", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NoSuchMechanismHere", code, StringComparison.Ordinal);

        var stripped = ConnectionFlowSourceGuardTests.StripComments(
            "A(); // PendingIntent.GetService(this, ExitRequestCode, x, y)\nB();");
        Assert.DoesNotContain("GetService", stripped, StringComparison.Ordinal);
        Assert.Contains("A();", stripped, StringComparison.Ordinal);
    }

    // ---- readers ---------------------------------------------------------------

    /// <summary>The service source with comments removed and string literals
    /// KEPT (the action string is the contract).</summary>
    private static string Source()
    {
        var path = Path.Combine(FindRepoRoot(), ServiceFile);
        Assert.True(File.Exists(path), "source missing: " + ServiceFile);
        return ConnectionFlowSourceGuardTests.StripComments(File.ReadAllText(path));
    }

    /// <summary>Everything from <c>OnStartCommand</c> to the next member, so
    /// the ordering assertion cannot accidentally read a StartForeground that
    /// lives somewhere else in the file.</summary>
    private static string OnStartCommandBody()
    {
        var code = Source();
        int start = code.IndexOf("public override StartCommandResult OnStartCommand", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnStartCommand not found");
        int end = code.IndexOf("public override void OnDestroy", StringComparison.Ordinal);
        Assert.True(end > start, "OnDestroy not found after OnStartCommand");
        return code[start..end];
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
