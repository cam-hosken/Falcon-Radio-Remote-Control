using Falcon.App.Core.Services;

namespace Falcon.App.Tests;

/// <summary>
/// The §5 confirmation fake's own contract (UI tweaks round 10, P1).
///
/// <para>The fake is test INFRASTRUCTURE, and infrastructure that lies is
/// worse than none: every P3 lifecycle pin — "the session dropped while the
/// prompt was open", "the prompt faulted", "the operator cancelled" — is only
/// as trustworthy as the handle it drives. If <c>ConfirmAsync</c> quietly
/// answered by itself, a consumer that ignored the answer entirely would still
/// look correct. So the handle semantics are pinned here, once, where they can
/// fail on their own.</para>
/// </summary>
public class FakeConfirmationPromptTests
{
    private static IConfirmationPrompt Seam(FakeConfirmationPrompt fake) => fake;

    [Fact]
    public void EveryCall_IsRecordedVerbatim_InOrder()
    {
        var fake = new FakeConfirmationPrompt();

        Seam(fake).ConfirmAsync("Delete BOB?", "The radio will remove this address from its book.",
            "Delete", "Cancel");
        Seam(fake).ConfirmAsync("Clear net 3?", "The radio wipes this net's ID, type and frequencies.",
            "Clear", "Cancel");

        Assert.Equal(2, fake.CallCount);

        var first = fake.Prompts[0];
        Assert.Equal("Delete BOB?", first.Title);
        Assert.Equal("The radio will remove this address from its book.", first.Message);
        Assert.Equal("Delete", first.AcceptText);
        Assert.Equal("Cancel", first.CancelText);

        Assert.Equal("Clear net 3?", fake.Last.Title);
        Assert.Equal("Clear", fake.Last.AcceptText);
    }

    [Fact]
    public async Task APromptStaysPending_UntilTheHandleIsDriven()
    {
        // The whole reason the fake is controllable: the test gets to change
        // the world while the question is still on screen.
        var fake = new FakeConfirmationPrompt();

        var task = Seam(fake).ConfirmAsync("Erase every ALE address?", "…", "Erase", "Cancel");

        Assert.False(task.IsCompleted);
        Assert.False(fake.Last.IsResolved);

        fake.Last.Complete(true);

        Assert.True(task.IsCompleted);
        Assert.True(fake.Last.IsResolved);
        Assert.True(await task);
    }

    [Fact]
    public async Task Complete_False_IsTheCancelAnswer()
    {
        var fake = new FakeConfirmationPrompt();
        var task = Seam(fake).ConfirmAsync("Delete CAM?", "…", "Delete", "Cancel");

        fake.Last.Complete(false);

        Assert.False(await task);
    }

    /// <summary>§5 names <c>Fault()</c> with no arguments, and it must produce
    /// a genuinely faulted task — not merely a flag. Awaiting it has to throw,
    /// because that is what a P3 consumer's <c>await</c> will do, and the
    /// lifecycle contract says such a consumer sends nothing and does not
    /// wedge.</summary>
    [Fact]
    public async Task Fault_FaultsTheTask()
    {
        var fake = new FakeConfirmationPrompt();
        var task = Seam(fake).ConfirmAsync("Delete CAM?", "…", "Delete", "Cancel");

        fake.Last.Fault();

        Assert.True(task.IsFaulted);
        Assert.True(fake.Last.IsResolved);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public void Cancel_CancelsTheTask()
    {
        var fake = new FakeConfirmationPrompt();
        var task = Seam(fake).ConfirmAsync("Delete CAM?", "…", "Delete", "Cancel");

        fake.Last.Cancel();

        Assert.True(task.IsCanceled);
    }

    [Fact]
    public async Task EnqueueAnswer_AnswersTheNextCall_OnePerCall_InOrder()
    {
        var fake = new FakeConfirmationPrompt();
        fake.EnqueueAnswer(true);
        fake.EnqueueAnswer(false);

        var first = Seam(fake).ConfirmAsync("a", "…", "Yes", "No");
        var second = Seam(fake).ConfirmAsync("b", "…", "Yes", "No");
        var third = Seam(fake).ConfirmAsync("c", "…", "Yes", "No");

        Assert.True(first.IsCompleted);
        Assert.True(await first);
        Assert.True(second.IsCompleted);
        Assert.False(await second);
        // The queue is spent: the third call is a normal pending prompt, not a
        // repeat of the last answer.
        Assert.False(third.IsCompleted);
        Assert.False(fake.Prompts[2].IsResolved);
    }

    [Fact]
    public void EnqueuedAnswers_AreStillRecordedCalls()
    {
        // The shorthand must not skip the recording — the §5 prompt table is
        // asserted on the strings even when the answer is immediate.
        var fake = new FakeConfirmationPrompt();
        fake.EnqueueAnswer(true);

        Seam(fake).ConfirmAsync("Erase every ALE address?",
            "The radio clears all selfs, individuals, nets and LQA schedules. Channel groups and messages survive.",
            "Erase", "Cancel");

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("Erase", fake.Last.AcceptText);
        Assert.True(fake.Last.IsResolved);
    }

    [Fact]
    public void AnsweringTwice_Throws_RatherThanSilentlyIgnoringTheSecond()
    {
        var fake = new FakeConfirmationPrompt();
        Seam(fake).ConfirmAsync("Delete CAM?", "…", "Delete", "Cancel");

        fake.Last.Complete(true);

        Assert.Throws<InvalidOperationException>(() => fake.Last.Complete(false));
        Assert.Throws<InvalidOperationException>(() => fake.Last.Cancel());
    }

    [Fact]
    public void Last_BeforeAnyCall_Throws_NeverReturnsAPhantomPrompt()
    {
        var fake = new FakeConfirmationPrompt();
        Assert.Equal(0, fake.CallCount);
        Assert.Throws<InvalidOperationException>(() => fake.Last);
    }

    /// <summary>
    /// The §5 fake's API SURFACE, pinned by reflection: <c>Complete(bool)</c>,
    /// <c>Fault()</c>, <c>Cancel()</c>, <c>EnqueueAnswer(bool)</c> — each
    /// existing exactly once and taking exactly that.
    ///
    /// <para><b>Why the behavioural tests above are not enough.</b> Every one
    /// of them CALLS these members, and a call site keeps compiling when a
    /// member grows an optional parameter: restoring
    /// <c>Fault(Exception? error = null)</c> left all 855 App tests green (P1
    /// audit round 2). But an optional parameter is a different seam — the P3
    /// consumers and their lifecycle pins are written against the literal §5
    /// contract, and a fake that accepts more than the contract lets a
    /// consumer be written against something the plan never promised. Arity is
    /// only visible to reflection, so that is what checks it.</para>
    ///
    /// <para>Overload COUNT is asserted too, so adding a second
    /// <c>Fault(Exception)</c> beside the clean one fails here as well — the
    /// same distinction the baud signature pins draw between an added overload
    /// and an arity change.</para>
    /// </summary>
    [Fact]
    public void TheFakeContractFamily_HasExactlyTheParameterLists_PlanSection5Names()
    {
        AssertSingleOverloadTaking(typeof(PendingPrompt), nameof(PendingPrompt.Complete), typeof(bool));
        AssertSingleOverloadTaking(typeof(PendingPrompt), nameof(PendingPrompt.Fault));
        AssertSingleOverloadTaking(typeof(PendingPrompt), nameof(PendingPrompt.Cancel));
        AssertSingleOverloadTaking(typeof(FakeConfirmationPrompt),
            nameof(FakeConfirmationPrompt.EnqueueAnswer), typeof(bool));
    }

    /// <summary>Anti-vacuity partner: the helper must be able to FAIL, on a
    /// name that is not there and on a real member whose parameter list is not
    /// the asserted one. Otherwise "the surface is contract-exact" could just
    /// mean "the reflection found nothing to disagree with".</summary>
    [Fact]
    public void TheArityHelper_FailsOnAMissingNameAndOnAWrongParameterList()
    {
        Assert.ThrowsAny<Exception>(() =>
            AssertSingleOverloadTaking(typeof(PendingPrompt), "NoSuchMember"));
        // Complete(bool) really exists — asserting it takes nothing must fail...
        Assert.ThrowsAny<Exception>(() =>
            AssertSingleOverloadTaking(typeof(PendingPrompt), nameof(PendingPrompt.Complete)));
        // ...and so must asserting the parameterless Fault takes an Exception,
        // which is precisely the deviation this family exists to catch.
        Assert.ThrowsAny<Exception>(() =>
            AssertSingleOverloadTaking(typeof(PendingPrompt), nameof(PendingPrompt.Fault), typeof(Exception)));
    }

    private static void AssertSingleOverloadTaking(Type owner, string name, params Type[] parameters)
    {
        var overloads = owner
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name)
            .ToList();

        Assert.True(overloads.Count == 1,
            $"{owner.Name}.{name}: expected exactly one overload, found {overloads.Count}");
        Assert.Equal(parameters, overloads[0].GetParameters().Select(p => p.ParameterType));
    }
}
