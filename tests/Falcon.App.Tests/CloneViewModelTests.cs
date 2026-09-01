using System.ComponentModel;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The Cloning card's ViewModel (plan round 11 §9A, reshaped by
/// plan-clone-field-round2 §3.3): the R-A identity TABLE, the two-level gating
/// with a VISIBLE reason, and the file seam the view drives. The campaigns themselves are <see cref="CloneServiceTests"/>' —
/// what is pinned here is what the OPERATOR sees and can press.
/// </summary>
public class CloneViewModelTests
{
    private static (CloneViewModel Vm, CloneService Clone, RadioSession Session, InjectingTransport Transport) Build()
    {
        var built = BuildAll();
        return (built.Vm, built.Clone, built.Session, built.Transport);
    }

    /// <summary>The same harness, plus the two handles a REAL run needs: the
    /// radio (its own timeouts) and the confirmation seam (a write that is
    /// never answered hangs forever, by the fake's design).</summary>
    private static (CloneViewModel Vm, CloneService Clone, RadioSession Session, InjectingTransport Transport,
        Prc138Radio Radio, FakeConfirmationPrompt Prompt) BuildAll()
    {
        // The inline context goes in BEFORE the radio is constructed:
        // Prc138Radio captures SynchronizationContext.Current there, and a
        // radio built without one posts its state changes to the thread pool —
        // which makes every assertion below a race.
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        var transport = new InjectingTransport();
        var prompt = new FakeConfirmationPrompt();
        var radio = new Prc138Radio(transport);
        var session = new RadioSession(radio, transport);
        var clone = new CloneService(
            radio, session, prompt,
            new SsbSurface(radio), new PowerSurface(radio), new DeviceSurface(radio),
            new AleSurface(radio), new HopSurface(radio), new ChannelSurface(radio),
            new ModemSurface(radio), new ModeSurface(radio), new CampaignWireCoordinator());
        var vm = new CloneViewModel(clone, new AleSurface(radio), session);
        return (vm, clone, session, transport, radio, prompt);
    }

    // ---- R-A: the identity TABLE --------------------------------------------

    /// <summary>A COMPLETE file the write gate really accepts. Since round 17
    /// F6 that means carrying the WHOLE 100-slot channel inventory: a
    /// <c>Read</c> marker over a short dump is DOWNGRADED at load, which is the
    /// pre-fix truncation and not a writable file.</summary>
    private static CloneFile Complete()
    {
        var file = CloneFileTests.Complete();
        CloneFileTests.FillChannels(file);
        return file;
    }

    /// <summary>The field's own book shape: several selfs, one of them the
    /// scan-gate self, and individuals a swap can pick from.</summary>
    private static CloneFile Roster()
    {
        var file = Complete();       // CAM (self), BOB (individual), NET2
        file.Selfs.Add(new CloneAddress { Name = "ALPHA", Group = 1 });
        file.Selfs.Add(new CloneAddress { Name = "HOS", Group = 3 });
        file.Individuals.Add(new CloneAddress { Name = "AAA", Group = 1, AssociatedSelf = "ALPHA" });
        return file;
    }

    [Fact]
    public void TheTable_IsOneRowPerSelf_InFileOrder_TitledByItsNets()
    {
        var (vm, clone, _, _) = Build();
        Assert.Empty(vm.SelfRows);

        var file = Roster();
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        Assert.Equal(["CAM", "ALPHA", "HOS"], vm.SelfRows.Select(r => r.SelfName));
        // C-1: a self a net names is TITLED by that net; a self no net names
        // keeps its own name.
        Assert.Equal(
            ["Net NET2 · group 2 · self CAM", "ALPHA", "HOS"],
            vm.SelfRows.Select(r => r.Title));
        Assert.Equal(["NET2"], vm.SelfRows[0].Nets);
        Assert.Empty(vm.SelfRows[1].Nets);

        // "(keep)" first, then this row's OWN individuals — AAA hangs off
        // ALPHA, so BOB (CAM's) is not on offer here.
        Assert.Equal(["(keep)", "AAA"], vm.SelfRows[1].SwapChoices);
        Assert.Equal(SelfRowViewModel.KeepChoice, vm.SelfRows[1].SwapChoices[0]);
    }

    /// <summary>C-1 on the OWNER's own fill — §13.5's gate, row by row.</summary>
    [Fact]
    public void TheTable_OnTheOwnersFill_IsThreeRowsTitledByTheirNets_WithScopedPickers()
    {
        var (vm, clone, _, _) = Build();
        var file = CloneSwapTests.OwnerFill();
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        Assert.Equal(3, vm.SelfRows.Count);
        Assert.Equal(
            ["HOS", "Net HFL · group 2 · self W6HOS", "Net HFN · group 1 · self W6HOS1"],
            vm.SelfRows.Select(r => r.Title));

        // HFL's row offers HFL's own three; HFN's row offers HFN's own two.
        var hfl = Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS");
        Assert.Equal(["(keep)", "KI6EZA1", "KC1HAS", "KG6KMJ"], hfl.SwapChoices);
        Assert.True(hfl.OffersSwap);
        var hfn = Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS1");
        Assert.Equal(["(keep)", "N7BOI", "N5PWU"], hfn.SwapChoices);

        // The scan-gate self is Replace-only and titled by its own name — no
        // net names it.
        var gate = Assert.Single(vm.SelfRows, r => r.SelfName == "HOS");
        Assert.True(gate.IsScanGateSelf);
        Assert.False(gate.OffersSwap);
        Assert.Empty(gate.Nets);
    }

    [Fact]
    public void ASelfSeveralNetsName_IsOneRow_TitledWithAllOfThem()
    {
        var (vm, clone, _, _) = Build();
        var file = CloneSwapTests.OwnerFill();
        file.Nets.Add(new CloneNet { Name = "HFM", Group = 2, AssociatedSelf = "W6HOS" });
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        // ONE slot, ONE disposition — so one row, however many nets name it.
        var row = Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS");
        Assert.Equal(["HFL", "HFM"], row.Nets);
        Assert.Equal("Nets HFL, HFM · self W6HOS", row.Title);
    }

    [Fact]
    public void ANetWhoseGroupChanged_RebuildsTheTitle()
    {
        // The rebuild guard exists so a campaign tick cannot throw away what
        // the operator is typing — but it must not hold a STALE title either,
        // and a net's group is in the title while its name is not enough to
        // notice the change.
        var (vm, clone, _, _) = Build();
        clone.Adopt(CloneSwapTests.OwnerFill());
        vm.LoadJson(CloneSwapTests.OwnerFill().Save());
        Assert.Equal("Net HFL · group 2 · self W6HOS",
            Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS").Title);

        var regrouped = CloneSwapTests.OwnerFill();
        Assert.Single(regrouped.Nets, n => n.Name == "HFL").Group = 4;
        vm.LoadJson(regrouped.Save());

        Assert.Equal("Net HFL · group 4 · self W6HOS",
            Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS").Title);
    }

    [Fact]
    public void ASelfWithNoCandidateOfItsOwn_ShowsNoPickerAtAll()
    {
        var (vm, clone, _, _) = Build();
        var file = CloneSwapTests.OwnerFill();
        // HFN's self loses both of its individuals: an empty picker is a
        // control that cannot do anything, so the row offers the entry only.
        file.Individuals.RemoveAll(i => i.AssociatedSelf == "W6HOS1");
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        var row = Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS1");
        Assert.False(row.OffersSwap);
        Assert.Equal([SelfRowViewModel.KeepChoice], row.SwapChoices);
        // ANTI-VACUITY: the row that still has candidates still offers them.
        Assert.True(Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS").OffersSwap);
    }

    // ---- C-Q5 / C-3: when the card offers no rows at all ---------------------

    [Fact]
    public void AFileWhoseBookWasNotRead_OffersNoRows_SaysWhy_AndCannotBeWritten()
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        var file = CloneSwapTests.OwnerFill();
        file.BookState = CloneDomainState.Faulted;
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        Assert.Empty(vm.SelfRows);
        Assert.True(vm.ShowsBookNotRead);
        Assert.False(vm.CanWrite);
        Assert.Contains("address book", vm.WriteGateReason, StringComparison.Ordinal);

        // ANTI-VACUITY: the same file with the book READ gets its three rows
        // and the caption goes away.
        vm.LoadJson(CloneSwapTests.OwnerFill().Save());
        Assert.Equal(3, vm.SelfRows.Count);
        Assert.False(vm.ShowsBookNotRead);
    }

    [Fact]
    public void AReadButEmptyBook_StillGetsTheSyntheticRow()
    {
        var (vm, clone, _, _) = Build();
        var file = CloneSwapTests.OwnerFill();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        file.Schedules.Clear();
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        Assert.False(vm.ShowsBookNotRead);
        var row = Assert.Single(vm.SelfRows);
        Assert.True(row.IsSyntheticRow);
        Assert.Equal("No self — new name required", row.Title);
    }

    [Fact]
    public void AFileWhoseFirstSelfIsTooLong_ShowsTheFillGateCaption_AndCannotBeWritten()
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        var file = CloneSwapTests.OwnerFill();
        file.Selfs.RemoveAll(s => s.Name == "HOS");         // the 1-3 character self goes
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        // C-3/C-D1: the write leads with the file's selfs in order, and the
        // radio refuses the whole book until a 1-3 character self is stored.
        Assert.Equal(
            "The radio needs a 1-3 character self first — the first self in this file is W6HOS.",
            vm.FillGateReason);
        Assert.True(vm.HasFillGateReason);
        Assert.False(vm.CanWrite);
        Assert.Equal(vm.FillGateReason, vm.WriteGateReason);
        // The rows are still offered — the operator repairs the file by
        // renaming a self, which is what the rows are for.
        Assert.Equal(2, vm.SelfRows.Count);

        // ANTI-VACUITY: the owner's own fill leads with HOS and is writable.
        vm.LoadJson(CloneSwapTests.OwnerFill().Save());
        Assert.Equal("", vm.FillGateReason);
        Assert.False(vm.HasFillGateReason);
        Assert.True(vm.CanWrite);
    }

    [Fact]
    public void TheScanGateRow_OffersNoSwap_AndCarriesTheCaptionTheDocQuotes()
    {
        var (vm, clone, _, _) = Build();
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());

        var gate = Assert.Single(vm.SelfRows, r => r.SelfName == "HOS");
        Assert.True(gate.IsScanGateSelf);
        Assert.False(gate.OffersSwap);
        Assert.Equal(SelfRowViewModel.ScanGateNameLength, gate.NameLength);

        // …and a self that is not the scan-gate one does offer it.
        var alpha = Assert.Single(vm.SelfRows, r => r.SelfName == "ALPHA");
        Assert.False(alpha.IsScanGateSelf);
        Assert.True(alpha.OffersSwap);
        Assert.Equal(SelfRowViewModel.MaxNameLength, alpha.NameLength);

        Assert.Equal(
            "Scan-gate self: replace with a 1-3 character name; swapping is not offered.",
            SelfRowViewModel.ScanGateCaption);
    }

    [Fact]
    public void TheNoSelfFile_GetsTheSyntheticRow_A6()
    {
        var (vm, clone, _, _) = Build();
        var file = Complete();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        var row = Assert.Single(vm.SelfRows);
        Assert.Equal("", row.SelfName);
        Assert.True(row.IsSyntheticRow);
        Assert.Equal("No self — new name required", row.Title);
        // Replace-only, and NOT the scan-gate row: a post-ERASE file takes any
        // valid 1-15 name.
        Assert.False(row.OffersSwap);
        Assert.False(row.IsScanGateSelf);
        Assert.Equal(SelfRowViewModel.MaxNameLength, row.NameLength);
    }

    // ---- Row exclusivity (A-1) ----------------------------------------------

    [Fact]
    public void ARowIsKeep_UntilOneOfItsTwoControlsIsUsed()
    {
        var (vm, clone, _, _) = Build();
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());
        var row = Assert.Single(vm.SelfRows, r => r.SelfName == "ALPHA");   // the row that offers both

        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.Keep, null), row.ToDisposition());

        // Keep → Swap.
        row.SwapSelection = "BOB";
        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.SwapWithIndividual, "BOB"), row.ToDisposition());

        // Swap → Replace: typing clears the pick.
        row.ReplaceInput = "NEWNAME";
        Assert.Equal(SelfRowViewModel.KeepChoice, row.SwapSelection);
        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.Replace, "NEWNAME"), row.ToDisposition());

        // Replace → Swap: picking clears the typing.
        row.SwapSelection = "AAA";
        Assert.Equal("", row.ReplaceInput);
        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.SwapWithIndividual, "AAA"), row.ToDisposition());

        // …and clearing BOTH is Keep again — including the explicit "(keep)"
        // position, which means the same thing as no selection at all.
        row.SwapSelection = SelfRowViewModel.KeepChoice;
        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.Keep, null), row.ToDisposition());
        row.ReplaceInput = "  ";
        Assert.Equal(new SelfDisposition("ALPHA", SelfDispositionKind.Keep, null), row.ToDisposition());
    }

    [Fact]
    public void TheScanGateRow_RefusesAReplacementLongerThanThree_D2()
    {
        var (vm, clone, _, _) = Build();
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());
        var gate = Assert.Single(vm.SelfRows, r => r.SelfName == "HOS");

        gate.ReplaceInput = "HOSTS";
        // The MaxLength is markup and markup is not the contract, so the row
        // enforces it too.
        Assert.Equal("HOS", gate.ReplaceInput);
        Assert.Equal(new SelfDisposition("HOS", SelfDispositionKind.Replace, "HOS"), gate.ToDisposition());

        // ANTI-VACUITY: a row that is NOT the scan-gate one keeps the long name.
        var alpha = Assert.Single(vm.SelfRows, r => r.SelfName == "ALPHA");
        alpha.ReplaceInput = "BASECAMP";
        Assert.Equal("BASECAMP", alpha.ReplaceInput);
    }

    [Fact]
    public void TheTableIsRebuiltOnlyWhenTheFileChanges_SoTypingSurvivesACampaignTick()
    {
        var (vm, clone, _, _) = Build();
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());
        vm.SelfRows[0].ReplaceInput = "NEW";

        // A campaign tick (any Changed on the service) re-renders the card.
        clone.Adopt(Roster());
        Assert.Equal("NEW", vm.SelfRows[0].ReplaceInput);

        // …but a file with a DIFFERENT set of selfs really does rebuild.
        vm.LoadJson(Complete().Save());
        Assert.Equal(["CAM"], vm.SelfRows.Select(r => r.SelfName));
        Assert.Equal("", vm.SelfRows[0].ReplaceInput);
    }

    // ---- IdentityError mirrors the transform's own refusal --------------------

    [Theory]
    [InlineData("BAD NAME", "BAD NAME is not a name this radio can store — an ALE name is 1-15 letters or digits.")]
    [InlineData(" net2 ", "NET2 is already a net in this file — an ALE name is unique across selfs, "
        + "individuals and nets, so it cannot also be this radio's self.")]
    [InlineData("CAM", "CAM is already in this file's address book once the change is made — "
        + "an ALE name is unique across selfs, individuals and nets.")]
    public void TheIdentityError_IsTheTransformsOwnRefusal_Live(string typed, string expected)
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());
        Assert.True(vm.CanWrite);

        // ALPHA's row, not CAM's: CAM is 1-3 characters, so its Entry would
        // clip the long names these cases are about (D2).
        var row = Assert.Single(vm.SelfRows, r => r.SelfName == "ALPHA");
        row.ReplaceInput = typed;

        Assert.Equal(expected, vm.IdentityError);
        // …byte for byte the same sentence the write preflight would give.
        Assert.Equal(CloneSwap.Refusal(clone.File!, vm.Dispositions), vm.IdentityError);
        Assert.True(vm.HasIdentityError);
        Assert.False(vm.CanWrite);
        Assert.Equal(expected, vm.WriteGateReason);

        // …and a usable name clears it, so the block is the table and not the
        // typing.
        row.ReplaceInput = "BRAND";
        Assert.Equal("", vm.IdentityError);
        Assert.True(vm.CanWrite);
    }

    [Fact]
    public void TheNoSelfFile_BlocksWriteUntilTheSyntheticRowIsFilledIn()
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        var file = Complete();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        clone.Adopt(file);
        vm.LoadJson(file.Save());

        Assert.Equal(CloneService.NoSelfRejection, vm.IdentityError);
        Assert.False(vm.CanWrite);

        vm.SelfRows[0].ReplaceInput = "NEW";
        Assert.Equal("", vm.IdentityError);
        Assert.True(vm.CanWrite);
    }

    [Fact]
    public void EveryBoundRowProperty_RaisesPropertyChanged()
    {
        var (vm, clone, _, _) = Build();
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());
        var row = vm.SelfRows[0];

        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        row.SwapSelection = "BOB";
        row.ReplaceInput = "NW1";

        Assert.Contains(nameof(SelfRowViewModel.SwapSelection), raised);
        Assert.Contains(nameof(SelfRowViewModel.ReplaceInput), raised);
    }

    [Fact]
    public void ARowEdit_RaisesTheCardsOwnNotifications()
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        clone.Adopt(Roster());
        vm.LoadJson(Roster().Save());

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Single(vm.SelfRows, r => r.SelfName == "ALPHA").ReplaceInput = " net2 ";
        Assert.True(vm.HasIdentityError);

        Assert.Contains(nameof(CloneViewModel.IdentityError), raised);
        Assert.Contains(nameof(CloneViewModel.HasIdentityError), raised);
        Assert.Contains(nameof(CloneViewModel.CanWrite), raised);
        Assert.Contains(nameof(CloneViewModel.WriteGateReason), raised);
        Assert.Contains(nameof(CloneViewModel.HasWriteGateReason), raised);
    }

    [Fact]
    public void TheCaptionsAndTheirVisibility_AreRaisedWhenTheFileChanges()
    {
        // Both C additions are BOUND (the card's two captions), so both must be
        // raised or the card renders the previous file's state.
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);
        clone.Adopt(CloneSwapTests.OwnerFill());
        vm.LoadJson(CloneSwapTests.OwnerFill().Save());

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var faulted = CloneSwapTests.OwnerFill();
        faulted.BookState = CloneDomainState.Faulted;
        vm.LoadJson(faulted.Save());

        Assert.Contains(nameof(CloneViewModel.ShowsBookNotRead), raised);
        Assert.Contains(nameof(CloneViewModel.FillGateReason), raised);
        Assert.Contains(nameof(CloneViewModel.HasFillGateReason), raised);
        Assert.Contains(nameof(CloneViewModel.CanWrite), raised);
        Assert.Contains(nameof(CloneViewModel.WriteGateReason), raised);
        Assert.Contains(nameof(CloneViewModel.HasWriteGateReason), raised);
    }

    // ---- Gating (§9A + the standing two-level policy) ----------------------

    [Fact]
    public void BothCampaigns_AreGreyWhileDisconnected_WithTheReasonOnScreen()
    {
        var (vm, _, _, _) = Build();

        Assert.False(vm.CanRead);
        Assert.False(vm.CanWrite);
        Assert.True(vm.HasReadGateReason);
        Assert.True(vm.HasWriteGateReason);
        Assert.Equal("Not connected.", vm.ReadGateReason);
        Assert.Equal("Not connected.", vm.WriteGateReason);
    }

    [Fact]
    public void WriteStaysGrey_UntilAFileWithEveryDomainReadIsLoaded()
    {
        var (vm, clone, session, transport) = Build();
        ConnectReady(session, transport);

        Assert.True(vm.CanRead);
        Assert.False(vm.CanWrite);
        Assert.Equal("No clone file loaded.", vm.WriteGateReason);

        var faulted = Complete();
        faulted.ChannelState = CloneDomainState.Faulted;
        clone.Adopt(faulted);
        vm.LoadJson(faulted.Save());
        Assert.False(vm.CanWrite);
        Assert.Contains("SSB channels", vm.WriteGateReason, StringComparison.Ordinal);

        vm.LoadJson(Complete().Save());
        Assert.True(vm.CanWrite);
        Assert.Equal("", vm.WriteGateReason);
    }

    [Fact]
    public void BothCampaigns_AreGreyWhileTheRadioIsOnTheAir()
    {
        var (vm, _, session, transport) = Build();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());
        Assert.True(vm.CanRead);
        Assert.True(vm.CanWrite);

        // ROUND 15 item I: an LQA is a minutes-long transmission (P14c) and
        // this card's private enum list could not see it — a campaign would
        // have started laps against a transmitting radio. The term is Core's
        // one predicate now.
        //
        // D11 (2026-08-29) REPLACED this test's opening move. It used to lead
        // with `SCANNING` as the first greying state; scanning is no longer a
        // gate term (see the two D11 pins below), so the on-air states are the
        // whole of what this test covers.
        Inject(transport, "SOUNDING W6HOS            CHANNEL: 30");
        Assert.False(vm.CanRead);
        Assert.False(vm.CanWrite);
        Assert.Equal(CloneViewModel.OnAirGateReason, vm.ReadGateReason);
        Assert.Equal(CloneViewModel.OnAirGateReason, vm.WriteGateReason);

        Inject(transport, "SCAN STOPPED");
        Assert.True(vm.CanRead);
        Assert.True(vm.CanWrite);
    }

    /// <summary>D11 (plan-clone-write-structural §2, owner order 2026-08-29
    /// after the phone install: "read file is still gated by scanning").
    /// SCANNING is not a gate term any more — D8 has the campaign issue an
    /// unconditional `ST` at every ALE occupancy and one restart attempt at its
    /// true end, so it stops and restores the scan itself. Gating the PRESS on
    /// scanning only made the operator do that by hand.</summary>
    [Fact]
    public void Scanning_GatesNeitherCampaign_BecauseTheCampaignStopsTheScanItself()
    {
        var (vm, _, session, transport) = Build();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        Inject(transport, "SCANNING");
        // The state really is CONFIRMED scanning — this is the exact line the
        // pre-D11 gate refused on, not an unconfirmed mirror.
        Assert.True(vm.CanRead);
        Assert.Equal("", vm.ReadGateReason);
        Assert.False(vm.HasReadGateReason);

        // …and the scan term never reached the write either: with a writable
        // file in hand, scanning alone leaves WRITE pressable.
        Assert.True(vm.CanWrite);
        Assert.Equal("", vm.WriteGateReason);
        Assert.False(vm.HasWriteGateReason);
    }

    /// <summary>D11's other half: the genuinely ON-AIR states still gate BOTH
    /// campaigns, and the reason is the REWORDED sentence — pinned as a
    /// literal, not through the constant, so a change to the wording fails here
    /// rather than silently re-labelling the card.</summary>
    [Fact]
    public void AConfirmedOnAirState_StillGatesBothCampaigns_WithTheRewordedSentence()
    {
        var (vm, _, session, transport) = Build();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        Inject(transport, "LINKED AAA");
        Assert.False(vm.CanRead);
        Assert.False(vm.CanWrite);
        Assert.Equal("The radio is on the air — stop it first.", vm.ReadGateReason);
        Assert.Equal("The radio is on the air — stop it first.", vm.WriteGateReason);
        Assert.True(vm.HasReadGateReason);
        Assert.True(vm.HasWriteGateReason);

        // The constant the card publishes IS that sentence (the view and
        // docs/ui.md read it from here).
        Assert.Equal("The radio is on the air — stop it first.", CloneViewModel.OnAirGateReason);
    }

    // ---- The file seam -----------------------------------------------------

    [Fact]
    public void ARejectedFile_LeavesThePreviousOneLoaded_AndReturnsTheReason()
    {
        var (vm, _, _, _) = Build();
        var good = Complete();
        good.Selfs.Add(new CloneAddress { Name = "KEEP", Group = 1 });
        Assert.Null(vm.LoadJson(good.Save()));
        Assert.True(vm.HasFile);
        Assert.Contains(vm.SelfRows, r => r.SelfName == "KEEP");

        var reason = vm.LoadJson(good.Save().Replace("falconclone/1", "falconclone/7", StringComparison.Ordinal));

        Assert.NotNull(reason);
        Assert.Contains("falconclone/7", reason, StringComparison.Ordinal);
        // The previous file is still the one in hand — a bad import is not a
        // way to lose a good read.
        Assert.Contains(vm.SelfRows, r => r.SelfName == "KEEP");
    }

    [Fact]
    public void BuildJson_IsNullUntilThereIsAFile_AndRoundTripsAfter()
    {
        var (vm, _, _, _) = Build();
        Assert.Null(vm.BuildJson());

        vm.LoadJson(Complete().Save());
        var json = vm.BuildJson();
        Assert.NotNull(json);
        Assert.Single(CloneFile.Load(json).Selfs);
    }

    /// <summary>The ONE shared file notice became TWO per-operation slots, each
    /// with its own error flag — so the read tab reports where the file went
    /// and the write tab reports what was opened, and neither overwrites the
    /// other. The rows themselves are pinned in the outcome-table tests
    /// below.</summary>
    [Fact]
    public void TheTwoFileNotices_AreWhereTheViewReportsItsOwnFileStep()
    {
        var (vm, _, _, _) = Build();
        Assert.False(vm.ShowsReadFileNotice);
        Assert.False(vm.ShowsOpenFileNotice);

        vm.NoteReadFileOutcome("saved: C:/somewhere/clone.falconclone.json",
            "clone.falconclone.json", isError: false);

        Assert.True(vm.ShowsReadFileNotice);
        Assert.Contains("saved", vm.ReadFileNotice, StringComparison.Ordinal);
        Assert.False(vm.ShowsOpenFileNotice);
    }

    // ========================================================================
    // THE TWO-TAB CARD (plan/plan-clone-pane-cleanup.md §6). What is pinned
    // here is the SPLIT: each operation owns its status line, its report and
    // its gate reason, the file line names the file in hand, and a stray
    // service event lands on the tab it belongs to.
    // ========================================================================

    // ---- D10: the tabs ------------------------------------------------------

    [Fact]
    public void TheCardOpensOnTheReadTab_AndTheOperatorsChoiceThenPersists()
    {
        var (vm, _, _, _) = Build();

        // The read is where the flow starts, so it is the construction default
        // — and the VM is a DI singleton behind a transient page, which is what
        // makes the choice below survive leaving the page and coming back.
        Assert.False(vm.IsWriteTabOpen);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.OpenWriteTabCommand.Execute(null);
        Assert.True(vm.IsWriteTabOpen);
        vm.OpenReadTabCommand.Execute(null);
        Assert.False(vm.IsWriteTabOpen);
        vm.OpenWriteTabCommand.Execute(null);
        Assert.True(vm.IsWriteTabOpen);

        // The tab strip's highlight is a DataTrigger on this property, so the
        // transitions have to be announced or the pressed button stays dark.
        Assert.Equal(3, raised.Count(n => n == nameof(CloneViewModel.IsWriteTabOpen)));
    }

    // ---- Status and report route to the operation that produced them --------

    [Fact]
    public async Task AReadsStatusAndReport_LandOnTheReadTab_AndAWritesOnTheWrite()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        await RealWriteAsync(vm, clone, radio, prompt);

        Assert.Equal(clone.StatusText, vm.WriteStatusText);
        Assert.NotEmpty(vm.WriteReportLines);
        Assert.True(vm.HasWriteReport);
        // The read tab has said nothing yet — under the single shared
        // StatusText/SummaryLines this same run wrote BOTH.
        Assert.Equal("", vm.ReadStatusText);
        Assert.Empty(vm.ReadReportLines);
        Assert.False(vm.HasReadReport);

        var writeReport = vm.WriteReportLines.ToList();
        var writeStatus = vm.WriteStatusText;

        await RealReadAsync(vm, clone, session, radio);

        Assert.Equal(clone.StatusText, vm.ReadStatusText);
        Assert.NotEmpty(vm.ReadReportLines);
        // …and the write's own account is exactly where the operator left it.
        Assert.Equal(writeReport, vm.WriteReportLines);
        Assert.Equal(writeStatus, vm.WriteStatusText);
    }

    /// <summary>
    /// D11 + THE ORDERING RULE. <c>CloneService.LoadJson</c> raises
    /// <c>Changed</c> SYNCHRONOUSLY, from inside itself — so the route for a
    /// load's own event has to be chosen BEFORE the call. It is the Write tab:
    /// that is where the loaded file is shown, and the round-17 downgrade
    /// notice must not vanish because a read happened to run first.
    /// </summary>
    [Fact]
    public async Task ALoadsDowngradeNotice_LandsOnTheWriteTab_EvenRightAfterARead()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);

        await RealReadAsync(vm, clone, session, radio);
        Assert.NotEmpty(vm.ReadReportLines);
        var readReport = vm.ReadReportLines.ToList();

        var stale = CloneFileTests.Complete();
        stale.CapturedUtc = "2026-08-22T19:42:03.0000000Z";
        for (int n = 0; n < 28; n++) stale.Channels.Add(new CloneChannel { Number = n });

        Assert.Null(vm.LoadJson(stale.Save()));

        const string notice =
            "SSB channels: this file predates the dump-completion fix (only 28 of 100 slots) "
            + "— re-read the radio.";
        Assert.Equal(notice, Assert.Single(vm.WriteReportLines));
        Assert.Contains(notice, vm.WriteStatusText, StringComparison.Ordinal);
        // …and the read's own report is untouched by someone opening a file.
        Assert.Equal(readReport, vm.ReadReportLines);
    }

    /// <summary><c>Adopt</c> is PUBLIC and nothing in the VM called it, so the
    /// only thing that can route its event is the handler's own
    /// reference-change detection. A file arriving from outside belongs to the
    /// Write tab, exactly like a load.</summary>
    [Fact]
    public async Task AnExternalAdopt_RoutesToTheWriteTab_EvenRightAfterARead()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        await RealReadAsync(vm, clone, session, radio);

        vm.ClearReadReportCommand.Execute(null);
        vm.ClearWriteReportCommand.Execute(null);
        Assert.NotEmpty(clone.Summary);          // there really is something to route

        clone.Adopt(Roster());

        Assert.Equal(clone.Summary, vm.WriteReportLines);
        Assert.Equal(clone.StatusText, vm.WriteStatusText);
        Assert.Empty(vm.ReadReportLines);
    }

    /// <summary>The fallback follows the NEWER operation in BOTH directions —
    /// which is what the <c>finally</c>-set <c>_lastRan</c> buys.</summary>
    [Fact]
    public async Task AStrayEvent_FollowsTheOperationThatRanLast_InBothDirections()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        // ---- write → read. Nothing but the READ's own `finally` can make
        // the fallback point at the read tab.
        await RealWriteAsync(vm, clone, radio, prompt);
        await RealReadAsync(vm, clone, session, radio);
        vm.ClearReadReportCommand.Execute(null);
        vm.ClearWriteReportCommand.Execute(null);
        Assert.NotEmpty(clone.Summary);

        // The SAME file instance: the handler sees no new reference, so this
        // is a pure fallback route and nothing else.
        clone.Adopt(clone.File!);
        Assert.NotEmpty(vm.ReadReportLines);
        Assert.Empty(vm.WriteReportLines);

        // ---- read → write, isolated the only way it can be: the read has to
        // leave a WRITABLE file behind, so it is the gate-closed read (which
        // installs nothing and keeps the loaded one). Nothing between it and
        // the write below routes anywhere — so only the WRITE's own `finally`
        // can move the fallback back to the write tab.
        var (vm2, clone2, session2, transport2, radio2, prompt2) = BuildAll();
        ConnectReady(session2, transport2);
        vm2.LoadJson(Complete().Save());
        var loaded = clone2.File;

        await GateClosedReadAsync(vm2, clone2, session2, radio2);
        Assert.Same(loaded, clone2.File);

        ConnectReady(session2, transport2);
        await RealWriteAsync(vm2, clone2, radio2, prompt2);
        vm2.ClearReadReportCommand.Execute(null);
        vm2.ClearWriteReportCommand.Execute(null);
        Assert.NotEmpty(clone2.Summary);

        clone2.Adopt(clone2.File!);
        Assert.NotEmpty(vm2.WriteReportLines);
        Assert.Empty(vm2.ReadReportLines);
    }

    // ---- D6: the report lifecycle ------------------------------------------

    [Fact]
    public async Task StartingARun_EmptiesItsOwnReportFirst_AndLeavesTheOtherAlone()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        await RealWriteAsync(vm, clone, radio, prompt);
        var first = vm.WriteReportLines.ToList();
        Assert.NotEmpty(first);

        await RealWriteAsync(vm, clone, radio, prompt);

        // REPLACED, not appended: an operator reading the list must never be
        // reading two runs at once.
        Assert.Equal(first, vm.WriteReportLines);
        Assert.Empty(vm.ReadReportLines);
    }

    [Fact]
    public async Task Clear_EmptiesOneReport_AndItsCanExecuteFollowsTheList()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        // Nothing to clear yet, so the button is dead.
        Assert.False(vm.ClearWriteReportCommand.CanExecute(null));
        Assert.False(vm.ClearReadReportCommand.CanExecute(null));

        await RealWriteAsync(vm, clone, radio, prompt);
        Assert.True(vm.ClearWriteReportCommand.CanExecute(null));
        Assert.False(vm.ClearReadReportCommand.CanExecute(null));

        bool canExecuteChanged = false;
        vm.ClearWriteReportCommand.CanExecuteChanged += (_, _) => canExecuteChanged = true;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ClearWriteReportCommand.Execute(null);

        Assert.Empty(vm.WriteReportLines);
        Assert.False(vm.HasWriteReport);
        Assert.Contains(nameof(CloneViewModel.HasWriteReport), raised);
        Assert.False(vm.ClearWriteReportCommand.CanExecute(null));
        Assert.True(canExecuteChanged,
            "the Clear button was never told it had nothing left to clear");
        // VM-side only: the service's own account is untouched, so a Clear is
        // never a way to lose the record the next event can still render.
        Assert.NotEmpty(clone.Summary);
    }

    /// <summary>The refresh SPLIT, stated as behaviour: a session-phase event
    /// moves the gates and may not rebuild a report the operator just
    /// cleared.</summary>
    [Fact]
    public async Task AClearedReport_StaysCleared_ThroughASessionEvent()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());
        await RealWriteAsync(vm, clone, radio, prompt);
        vm.ClearWriteReportCommand.Execute(null);
        Assert.NotEmpty(clone.Summary);          // …there really is something to rebuild FROM

        session.Close();

        Assert.Empty(vm.WriteReportLines);
        Assert.False(vm.HasWriteReport);
        // ANTI-VACUITY: the gates really did move on that same event.
        Assert.False(vm.CanRead);
        Assert.Equal("Not connected.", vm.ReadGateReason);
    }

    // ---- The one FILE LINE and its three states -----------------------------

    [Fact]
    public void TheFileLine_NamesTheFile_ItsOrigin_AndItsCounts()
    {
        var (vm, clone, _, _) = Build();
        Assert.Equal("No file loaded.", vm.FileLine);

        vm.LoadJson(Roster().Save());
        var counts = $"{clone.File!.Channels.Count} channel(s), {clone.File.Selfs.Count} self(s), "
            + $"{clone.File.Individuals.Count} individual(s), {clone.File.Nets.Count} net(s), "
            + $"{clone.File.Messages.Count} message(s)"
            // D5b: a COMPLETE file has all four other domains READ, so the
            // clause is present in full and in its fixed order.
            + " + HOP nets, presets, settings, lockouts";

        // A file in hand that nothing has NAMED yet still says where it is from.
        Assert.Equal($"Read from this radio (not saved) — {counts}", vm.FileLine);

        vm.NoteOpenFileOutcome("loaded: roster.falconclone.json", "roster.falconclone.json", false);
        Assert.Equal($"roster.falconclone.json — loaded from file — {counts}", vm.FileLine);

        vm.NoteReadFileOutcome("saved: C:/x/falcon-clone-20260828-101500.falconclone.json",
            "falcon-clone-20260828-101500.falconclone.json", false);
        Assert.Equal(
            $"falcon-clone-20260828-101500.falconclone.json — read from this radio — {counts}",
            vm.FileLine);
    }

    /// <summary>
    /// D5b (plan-clone-write-structural.md §5.5) — THE OTHER-DOMAINS CLAUSE,
    /// byte-exact and in its fixed order.
    ///
    /// <para>THE COMPLAINT IT ANSWERS: a clone of an EMPTY-FILL radio counts
    /// zero of four of the five counted domains, so the line read "0 self(s),
    /// 0 individual(s), 0 net(s), 2 channel(s), 0 message(s)" and the operator
    /// reasonably concluded the clone had captured only channels. It had also
    /// captured the settings, the presets, the HOP nets and the lockouts.</para>
    /// </summary>
    [Fact]
    public void TheFileLine_NamesTheOtherDomainsItRead_InOneFixedOrder()
    {
        var (vm, _, _, _) = Build();
        var file = Complete();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        file.Schedules.Clear();
        file.Messages.Clear();
        vm.LoadJson(file.Save());

        // The blank-fill radio the complaint was about — and it no longer
        // reads as "only channels".
        Assert.Equal(
            "Read from this radio (not saved) — 100 channel(s), 0 self(s), 0 individual(s), "
                + "0 net(s), 0 message(s) + HOP nets, presets, settings, lockouts",
            vm.FileLine);
    }

    /// <summary>The clause lists exactly the subset whose marker is READ — so a
    /// domain that was NOT read is absent, in the same fixed order.</summary>
    [Fact]
    public void TheFileLine_ListsOnlyTheDomainsWhoseMarkerIsRead()
    {
        var (vm, clone, _, _) = Build();
        var file = Complete();
        file.ModemState = CloneDomainState.Faulted;
        file.Lockouts!.State = CloneDomainState.Unread;

        // NOTIFICATION: the clause reads MORE of the file than the counts did,
        // so the line has to be re-raised wherever the file in hand changes —
        // it is, on the same event the counts always rode.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        clone.Adopt(file);
        Assert.Contains(nameof(vm.FileLine), raised);

        Assert.EndsWith(" message(s) + HOP nets, settings", vm.FileLine, StringComparison.Ordinal);
    }

    /// <summary>…and the clause is OMITTED ENTIRELY when the subset is empty:
    /// a trailing " + " with nothing after it would be worse than saying
    /// nothing.</summary>
    [Fact]
    public void TheFileLine_OmitsTheClauseEntirely_WhenNoOtherDomainWasRead()
    {
        var (vm, clone, _, _) = Build();
        var file = Complete();
        file.SettingState = CloneDomainState.Unread;
        file.ModemState = CloneDomainState.Unread;
        file.HopNetState = CloneDomainState.Unread;
        file.Lockouts!.State = CloneDomainState.Unread;
        clone.Adopt(file);

        Assert.EndsWith(" message(s)", vm.FileLine, StringComparison.Ordinal);
        Assert.DoesNotContain(" + ", vm.FileLine, StringComparison.Ordinal);
    }

    /// <summary>THE STALE-NAME HOLE. A read that installed a DIFFERENT file is
    /// new radio contents, and the previously opened file's name may not travel
    /// onto it — not even when the save that would have named it fails.</summary>
    [Fact]
    public async Task AReadThatInstalledANewFile_DropsThePreviousFilesName_EvenWhenTheSaveFails()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Roster().Save());
        vm.NoteOpenFileOutcome("loaded: A.falconclone.json", "A.falconclone.json", false);
        Assert.StartsWith("A.falconclone.json — loaded from file — ", vm.FileLine);

        await RealReadAsync(vm, clone, session, radio);

        Assert.DoesNotContain("A.falconclone.json", vm.FileLine, StringComparison.Ordinal);
        Assert.StartsWith("Read from this radio (not saved) — ", vm.FileLine);

        vm.NoteReadFileOutcome("save failed: the disk is full", null, true);

        Assert.StartsWith("Read from this radio (not saved) — ", vm.FileLine);
        Assert.True(vm.ShowsReadFileError);
        Assert.False(vm.ShowsReadFileNotice);
    }

    /// <summary>…and its TWIN, which is why the reset is conditioned on the
    /// file REFERENCE: <c>CloneService.ReadAsync</c> can come back without
    /// installing anything (its own gate closing between the VM's check and
    /// its own), and a same-reference return must keep the loaded file's
    /// identity.</summary>
    [Fact]
    public async Task AReadWhoseGateClosedWithoutInstallingAFile_KeepsTheLoadedFilesName()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Roster().Save());
        vm.NoteOpenFileOutcome("loaded: A.falconclone.json", "A.falconclone.json", false);
        var before = clone.File;

        await GateClosedReadAsync(vm, clone, session, radio);

        Assert.Same(before, clone.File);
        Assert.StartsWith("A.falconclone.json — loaded from file — ", vm.FileLine);
    }

    /// <summary>
    /// THE STALE-READ GUARD's half of the seam (audit round 1). The view saves
    /// whatever <c>BuildJson</c> hands it, so it has to be able to ask whether
    /// the read installed anything at all: a read that installed NOTHING left
    /// the previously loaded file in hand, and saving that as
    /// "read from this radio" is how stale settings reach a radio.
    /// </summary>
    [Fact]
    public async Task AReadSaysWhetherItInstalledAFile_SoTheViewNeverSavesAStaleOne()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        // Nothing has read yet, so there is nothing to save.
        Assert.False(vm.LastReadInstalledNewFile);

        var loaded = clone.File;
        await RealReadAsync(vm, clone, session, radio);
        Assert.NotSame(loaded, clone.File);
        Assert.True(vm.LastReadInstalledNewFile);

        // …and the gate-closed read, which installs nothing: the file in hand
        // is still the LOADED one, so the view must not save it.
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());
        var kept = clone.File;
        await GateClosedReadAsync(vm, clone, session, radio);

        Assert.Same(kept, clone.File);
        Assert.False(vm.LastReadInstalledNewFile);
    }

    /// <summary>
    /// …and the flag describes the last ATTEMPT, not the last completed run
    /// (audit round 2). The preflight returns ahead of everything else, so a
    /// blocked attempt that inherited the PREVIOUS read's <c>true</c> would
    /// send the view off to save that read's file a second time under a fresh
    /// name — the same defect through a different door.
    /// </summary>
    [Fact]
    public async Task ABlockedReadAttempt_ClearsTheFlag_ItNeverInheritsTheLastReads()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        await RealReadAsync(vm, clone, session, radio);
        Assert.True(vm.LastReadInstalledNewFile);

        // That read left the session gone, so this attempt returns at the VM's
        // own preflight — the service is never even asked.
        Assert.False(vm.CanRead);
        var untouched = clone.File;
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Same(untouched, clone.File);
        Assert.False(vm.LastReadInstalledNewFile);
    }

    /// <summary>
    /// D6's replace-on-start reaches the NOTICE too (audit round 2, manager
    /// ruling). Starting a read is the next action of that slot's kind, so the
    /// previous read's "saved: …" may not stay on screen to be read as this
    /// read's outcome — least of all on the path where this read installs
    /// nothing and writes no line of its own.
    /// </summary>
    [Fact]
    public async Task StartingARead_ClearsTheReadTabsOwnNotice_OnBothPaths()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        // (1) the INSTALLING path.
        vm.NoteReadFileOutcome("saved: C:/x/old.falconclone.json", "old.falconclone.json", isError: false);
        Assert.True(vm.ShowsReadFileNotice);

        await RealReadAsync(vm, clone, session, radio);

        Assert.Equal("", vm.ReadFileNotice);
        Assert.False(vm.ShowsReadFileNotice);
        Assert.False(vm.ShowsReadFileError);

        // (2) the NO-INSTALL path — the one the stale line would have been read
        // against, since nothing replaces it afterwards.
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());
        vm.NoteReadFileOutcome("save failed: the disk is full", null, isError: true);
        Assert.True(vm.ShowsReadFileError);

        await GateClosedReadAsync(vm, clone, session, radio);

        Assert.False(vm.LastReadInstalledNewFile);
        Assert.Equal("", vm.ReadFileNotice);
        Assert.False(vm.ShowsReadFileNotice);
        Assert.False(vm.ShowsReadFileError);
    }

    /// <summary>
    /// Every identity TRANSITION announces the file line BY ITSELF. The
    /// accumulated-raise assertion in
    /// <see cref="EveryNewBoundProperty_RaisesPropertyChanged"/> could not see
    /// this: an earlier, unrelated <c>FileLine</c> raise satisfied it, and
    /// deleting the raise inside the identity setter left the whole suite green
    /// (audit round 1). So each act below is measured on a CLEARED list, and
    /// the two seam paths are the ones that isolate it — neither runs a gate
    /// refresh that would raise <c>FileLine</c> on their behalf.
    /// </summary>
    [Fact]
    public void TheFileLine_IsRaisedByTheIdentityTransitionItself()
    {
        var (vm, _, _, _) = Build();
        vm.LoadJson(Roster().Save());

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // (1) an OPEN that names the file.
        raised.Clear();
        vm.NoteOpenFileOutcome("loaded: roster.falconclone.json", "roster.falconclone.json", isError: false);
        Assert.Contains(nameof(CloneViewModel.FileLine), raised);
        Assert.StartsWith("roster.falconclone.json — loaded from file — ", vm.FileLine);

        // (2) a READ save that PROMOTES the name.
        raised.Clear();
        vm.NoteReadFileOutcome("saved: C:/x/clone.falconclone.json", "clone.falconclone.json", isError: false);
        Assert.Contains(nameof(CloneViewModel.FileLine), raised);
        Assert.StartsWith("clone.falconclone.json — read from this radio — ", vm.FileLine);

        // ANTI-VACUITY, and the proof that the raise comes from the TRANSITION
        // rather than from the seam method: an outcome that moves no identity
        // does not claim to have moved the file line.
        raised.Clear();
        vm.NoteOpenFileOutcome("open failed: access is denied", null, isError: true);
        Assert.DoesNotContain(nameof(CloneViewModel.FileLine), raised);
        Assert.StartsWith("clone.falconclone.json — read from this radio — ", vm.FileLine);
    }

    // ---- The §6 outcome table, row by row ----------------------------------

    /// <summary>The READ slot: text, error flag and whether the name is
    /// promoted. Each row is a line the view can actually produce (§7).</summary>
    [Theory]
    // D13: the save-location picker landed — the ONE row that promotes a name.
    [InlineData("stored: /storage/emulated/0/Download/clone.falconclone.json",
        "clone.falconclone.json", false, true)]
    // The picker's write failed: no name, and the error style.
    [InlineData("save failed: the disk is full", null, true, false)]
    // D13: a SHARE reports without promoting — the file went somewhere this app
    // cannot see, so the Write tab keeps saying "not saved".
    [InlineData("shared: clone.falconclone.json", null, false, false)]
    // …and a share that really failed IS an error now: under D13 nothing else
    // kept a copy, so the file did not leave the app.
    [InlineData("share failed: no app accepted it", null, true, false)]
    public void TheReadOutcomeSlot_CarriesItsTextItsStyleAndItsName(
        string message, string? storedName, bool isError, bool named)
    {
        var (vm, _, _, _) = Build();
        vm.LoadJson(Complete().Save());

        vm.NoteReadFileOutcome(message, storedName, isError);

        Assert.Equal(message, vm.ReadFileNotice);
        // TWO labels, ONE slot: a clean save must never render in the error
        // style, and an error must never render as an ordinary caption.
        Assert.Equal(!isError, vm.ShowsReadFileNotice);
        Assert.Equal(isError, vm.ShowsReadFileError);
        Assert.Equal(named, vm.FileLine.StartsWith("clone.falconclone.json — read from this radio — ",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// D13 (2026-08-30): THE PICKER IS THE PROMOTION POINT, AND THE ONLY ONE.
    /// The save-location picker's success is the single thing in the app that
    /// knows where a file really is — nothing writes a durable copy any more —
    /// so it is what names the file on the Write tab, and the name it set
    /// SURVIVES everything a later press can report.
    ///
    /// <para>Three things are pinned here, and each has its own way of going
    /// wrong. (1) A share reports without naming: promoting there would have the
    /// Write tab naming a file this app cannot find. (2) A share AFTER a store
    /// does not un-name the stored file: the file really is where the picker put
    /// it, and a share is not news about that. (3) A DISMISSED picker is silent
    /// — no notice, no error, nothing moved — which at this seam means the view
    /// makes NO CALL at all; the structural half (the bare `return` before any
    /// outcome row) is <c>CloningCardMarkupGuardTests</c>', and only the bench
    /// can prove the picker itself reports a dismissal that way.</para>
    /// </summary>
    [Fact]
    public void OnlyTheStoreOutcomeNamesTheFile_AndAShareNeitherNamesNorUnnamesIt_D13()
    {
        var (vm, _, _, _) = Build();
        vm.LoadJson(Complete().Save());

        // (1) A SHARE names nothing — the file line still says "not saved".
        vm.NoteReadFileOutcome("shared: falcon-clone-20260830-101500.falconclone.json", null, false);
        Assert.StartsWith("Read from this radio (not saved) — ", vm.FileLine);
        Assert.Null(vm.LastStoredFileName);

        // (2) THE PICKER LANDS: the name is promoted, onto the file line and
        // into the seed the next press offers.
        vm.NoteReadFileOutcome("stored: /somewhere/clone.falconclone.json", "clone.falconclone.json", false);
        Assert.StartsWith("clone.falconclone.json — read from this radio — ", vm.FileLine);
        Assert.Equal("clone.falconclone.json", vm.LastStoredFileName);

        // (3) …and a later share does not take it back. THE ROW THAT READS
        // BACKWARDS IF IT IS WRONG: the file IS where the picker put it.
        vm.NoteReadFileOutcome("shared: clone.falconclone.json", null, false);
        Assert.StartsWith("clone.falconclone.json — read from this radio — ", vm.FileLine);
        Assert.Equal("clone.falconclone.json", vm.LastStoredFileName);

        // …and neither does a failure.
        vm.NoteReadFileOutcome("share failed: no app accepted it", null, true);
        Assert.StartsWith("clone.falconclone.json — read from this radio — ", vm.FileLine);
        Assert.Equal("clone.falconclone.json", vm.LastStoredFileName);

        // A DISMISSED PICKER makes no call at all, so the slot is exactly what
        // the last press left — the error row above, untouched.
        Assert.Equal("share failed: no app accepted it", vm.ReadFileNotice);
        Assert.True(vm.ShowsReadFileError);
    }

    /// <summary>The OPEN slot. A REJECTED file leaves the identity alone,
    /// matching <c>LoadJson</c>'s keep-the-previous contract.</summary>
    [Fact]
    public void TheOpenOutcomeSlot_NamesACleanLoad_AndLeavesARejectionsIdentityAlone()
    {
        var (vm, _, _, _) = Build();
        vm.LoadJson(Complete().Save());

        vm.NoteOpenFileOutcome("loaded: good.falconclone.json", "good.falconclone.json", false);
        Assert.True(vm.ShowsOpenFileNotice);
        Assert.False(vm.ShowsOpenFileError);
        Assert.StartsWith("good.falconclone.json — loaded from file — ", vm.FileLine);

        // A rejection: the reason is the service's own sentence, in the error
        // style, and the file in hand is still the one that loaded.
        var rejection = vm.LoadJson(
            Complete().Save().Replace("falconclone/1", "falconclone/7", StringComparison.Ordinal));
        Assert.NotNull(rejection);
        vm.NoteOpenFileOutcome(rejection, null, true);

        Assert.Equal(rejection, vm.OpenFileNotice);
        Assert.False(vm.ShowsOpenFileNotice);
        Assert.True(vm.ShowsOpenFileError);
        Assert.StartsWith("good.falconclone.json — loaded from file — ", vm.FileLine);

        // …and an I/O failure reads the same way.
        vm.NoteOpenFileOutcome("open failed: access is denied", null, true);
        Assert.True(vm.ShowsOpenFileError);
        Assert.StartsWith("good.falconclone.json — loaded from file — ", vm.FileLine);
    }

    [Fact]
    public void ANewAttempt_ReplacesItsOwnSlot_AndLeavesTheOtherAlone()
    {
        var (vm, _, _, _) = Build();
        Assert.False(vm.ShowsReadFileNotice);
        Assert.False(vm.ShowsReadFileError);
        Assert.False(vm.ShowsOpenFileNotice);
        Assert.False(vm.ShowsOpenFileError);

        vm.NoteOpenFileOutcome("open failed: access is denied", null, true);
        vm.NoteReadFileOutcome("saved: C:/x/clone.falconclone.json", "clone.falconclone.json", false);

        Assert.True(vm.ShowsOpenFileError);
        Assert.True(vm.ShowsReadFileNotice);

        vm.NoteOpenFileOutcome("loaded: later.falconclone.json", "later.falconclone.json", false);
        Assert.Equal("loaded: later.falconclone.json", vm.OpenFileNotice);
        Assert.True(vm.ShowsOpenFileNotice);
        Assert.False(vm.ShowsOpenFileError);
        // …the read slot is exactly where it was.
        Assert.Equal("saved: C:/x/clone.falconclone.json", vm.ReadFileNotice);
        Assert.True(vm.ShowsReadFileNotice);
    }

    // ---- D12: the Store press and the name it re-shares --------------------

    /// <summary>
    /// D12 (owner report 2026-08-29). The Store press has its OWN gate: a file
    /// in hand and no operation running. It is deliberately NOT the read's
    /// gate — the export writes and shares a file the app already holds, so
    /// neither a missing radio nor a transmitting one has anything to say about
    /// it, and an operator who dismissed the share sheet must be able to reach
    /// the file while the radio is busy elsewhere.
    /// </summary>
    [Fact]
    public async Task TheStorePress_IsLive_ExactlyWhileAFileIsInHandAndNothingIsRunning()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();

        // (1) NO FILE: there is nothing to store, connected or not.
        Assert.False(vm.HasFile);
        Assert.False(vm.CanStore);
        ConnectReady(session, transport);
        Assert.False(vm.CanStore);

        // (2) A FILE IN HAND, nothing running — and the gate ANNOUNCES itself,
        // or the button stays grey over a file that is right there.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LoadJson(Complete().Save());
        Assert.Contains(nameof(CloneViewModel.CanStore), raised);
        Assert.True(vm.CanStore);

        // …and the second-level term the CAMPAIGNS share does not reach it: a
        // radio on the air is a reason not to lap its modes, not a reason to
        // withhold a file the app already has.
        Inject(transport, "LINKED AAA");
        Assert.False(vm.CanRead);
        Assert.False(vm.CanWrite);
        Assert.True(vm.CanStore);
        Inject(transport, "SCAN STOPPED");

        // (3) MID-WRITE and (4) MID-READ: an operation is moving the file the
        // export would read, so the press is grey for exactly as long as it
        // runs — and live again the moment it ends.
        bool? duringWrite = null;
        void OnRunning(object? sender, EventArgs e)
        {
            if (clone.IsRunning) duringWrite ??= vm.CanStore;
        }

        clone.Changed += OnRunning;
        await RealWriteAsync(vm, clone, radio, prompt);
        clone.Changed -= OnRunning;
        Assert.False(duringWrite);
        Assert.True(vm.CanStore);

        bool? duringRead = null;
        void OnReading(object? sender, EventArgs e)
        {
            if (clone.IsRunning) duringRead ??= vm.CanStore;
        }

        clone.Changed += OnReading;
        await RealReadAsync(vm, clone, session, radio);
        clone.Changed -= OnReading;
        Assert.False(duringRead);

        // The read left a (partial) file in hand, which is exactly the file an
        // operator who missed the share sheet is trying to get back.
        Assert.True(vm.HasFile);
        Assert.True(vm.CanStore);
    }

    /// <summary>
    /// THE IN-FLIGHT HALF OF THE GATE (audit round 1). The presses are
    /// <c>async void</c>, so a double-tap — or a tap in the gap between a read
    /// finishing and its export promoting the name — would start a SECOND
    /// export racing the first over the same paths and stacking a second share
    /// sheet on the first. The seam reports its in-flight state here, which is
    /// what greys the button rather than accepting a press it would drop.
    /// </summary>
    [Fact]
    public void AnExportInFlight_GreysTheStorePress_AndAnnouncesBothWays()
    {
        var (vm, _, _, _) = Build();
        vm.LoadJson(Complete().Save());
        Assert.True(vm.CanStore);
        Assert.False(vm.IsExporting);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetExporting(true);
        Assert.True(vm.IsExporting);
        Assert.False(vm.CanStore);
        Assert.Contains(nameof(CloneViewModel.IsExporting), raised);
        Assert.Contains(nameof(CloneViewModel.CanStore), raised);

        // …and the seam's `finally` hands it back, whatever the export did.
        raised.Clear();
        vm.SetExporting(false);
        Assert.False(vm.IsExporting);
        Assert.True(vm.CanStore);
        Assert.Contains(nameof(CloneViewModel.CanStore), raised);

        // A report that changes nothing announces nothing: the gate is not a
        // place to manufacture raises the card would re-render on.
        raised.Clear();
        vm.SetExporting(false);
        Assert.DoesNotContain(nameof(CloneViewModel.CanStore), raised);
    }

    /// <summary>
    /// THE NAME THE STORE PRESS RE-SHARES. Remembered from the one report that
    /// means "this file really is on disk under this name", so a second press
    /// re-shares the SAME stored file instead of leaving a timestamped copy on
    /// the phone every time.
    /// </summary>
    [Fact]
    public async Task TheStoredName_IsRemembered_SurvivesANoticeClear_AndNeverTravelsOntoAnotherFile()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        // (1) A file that was never persisted has no name to re-share — the
        // view mints a fresh one.
        Assert.Null(vm.LastStoredFileName);

        // (2) SET by the outcome that carries a name.
        vm.NoteReadFileOutcome("saved: C:/x/falcon-clone-20260829-101500.falconclone.json",
            "falcon-clone-20260829-101500.falconclone.json", isError: false);
        Assert.Equal("falcon-clone-20260829-101500.falconclone.json", vm.LastStoredFileName);

        // An outcome that promotes NO name leaves it alone: the file it names is
        // still on disk, and the operator can still ask for it.
        vm.NoteReadFileOutcome("save failed: the disk is full", null, isError: true);
        Assert.Equal("falcon-clone-20260829-101500.falconclone.json", vm.LastStoredFileName);

        // (3) SURVIVES THE NOTICE CLEAR. A read whose gate closed empties the
        // Read tab's slot and installs nothing — the file in hand, and its
        // stored copy, are exactly where they were.
        await GateClosedReadAsync(vm, clone, session, radio);
        Assert.Equal("", vm.ReadFileNotice);
        Assert.Equal("falcon-clone-20260829-101500.falconclone.json", vm.LastStoredFileName);

        // (4) RESET when a DIFFERENT file is installed. That file has never
        // been stored under this name, and a Store press that re-used it would
        // write these contents over the earlier read's file.
        clone.Adopt(Roster());
        Assert.Null(vm.LastStoredFileName);
    }

    /// <summary>…and the reset's dangerous half, on the path that actually
    /// happens in the field: read, store, read again. The second read's
    /// contents have never been stored under the first read's name.</summary>
    [Fact]
    public async Task AReadThatInstalledNewContents_DropsTheStoredName_SoStoreCannotOverwriteTheLastRead()
    {
        var (vm, clone, session, transport, radio, _) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());
        vm.NoteReadFileOutcome("saved: C:/x/falcon-clone-A.falconclone.json",
            "falcon-clone-A.falconclone.json", isError: false);
        Assert.Equal("falcon-clone-A.falconclone.json", vm.LastStoredFileName);

        var before = clone.File;
        await RealReadAsync(vm, clone, session, radio);

        Assert.NotSame(before, clone.File);
        Assert.Null(vm.LastStoredFileName);
    }

    // ---- The gate reasons, split ------------------------------------------

    [Fact]
    public void TheGateReasons_AreSplit_SoTheReadIsNeverGreyedByAFileItDoesNotNeed()
    {
        var (vm, _, session, transport) = Build();

        Assert.Equal("Not connected.", vm.ReadGateReason);
        Assert.Equal("Not connected.", vm.WriteGateReason);
        Assert.True(vm.HasReadGateReason);
        Assert.True(vm.HasWriteGateReason);

        ConnectReady(session, transport);

        // THE SPLIT'S WHOLE POINT: the read needs no file, so its caption is
        // empty where the write's names the missing one.
        Assert.Equal("", vm.ReadGateReason);
        Assert.False(vm.HasReadGateReason);
        Assert.Equal("No clone file loaded.", vm.WriteGateReason);
        Assert.True(vm.HasWriteGateReason);

        vm.LoadJson(Complete().Save());
        Assert.Equal("", vm.WriteGateReason);
        Assert.False(vm.HasWriteGateReason);

        // The shared second level greys BOTH, and says the same thing on both.
        // D11: the state that does that is an ON-AIR one — this line said
        // `SCANNING` until 2026-08-29.
        Inject(transport, "LINKED AAA");
        Assert.Equal(CloneViewModel.OnAirGateReason, vm.ReadGateReason);
        Assert.Equal(CloneViewModel.OnAirGateReason, vm.WriteGateReason);

        Inject(transport, "SCAN STOPPED");
        // …and the write's file-side terms are still asked in their old order.
        vm.LoadJson(CloneSwapTests.OwnerFill().Save());
        Assert.Single(vm.SelfRows, r => r.SelfName == "W6HOS").ReplaceInput = " net2 ";
        Assert.Equal(vm.IdentityError, vm.WriteGateReason);
        Assert.Equal("", vm.ReadGateReason);
    }

    /// <summary>D5: the operator's word is "read" or "write". "Campaign" is
    /// the code's word for it and stays there.</summary>
    [Fact]
    public async Task TheInProgressSentences_NameTheOperation_NotACampaign()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        string captured = "";
        void OnRunning(object? sender, EventArgs e)
        {
            if (clone.IsRunning && captured.Length == 0)
                captured = vm.ReadGateReason + "|" + vm.WriteGateReason;
        }

        clone.Changed += OnRunning;
        await RealWriteAsync(vm, clone, radio, prompt);
        clone.Changed -= OnRunning;
        Assert.Equal("A write is in progress.|A write is in progress.", captured);

        captured = "";
        clone.Changed += OnRunning;
        await RealReadAsync(vm, clone, session, radio);
        clone.Changed -= OnRunning;
        Assert.Equal("A read is in progress.|A read is in progress.", captured);
    }

    // ---- Notification: every new bound property announces itself ------------

    [Fact]
    public async Task EveryNewBoundProperty_RaisesPropertyChanged()
    {
        var (vm, clone, session, transport, radio, prompt) = BuildAll();
        ConnectReady(session, transport);
        vm.LoadJson(Complete().Save());

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await RealWriteAsync(vm, clone, radio, prompt);
        vm.NoteOpenFileOutcome("loaded: x.falconclone.json", "x.falconclone.json", false);
        vm.NoteReadFileOutcome("saved: C:/x/y.falconclone.json", "y.falconclone.json", false);

        foreach (var name in new[]
                 {
                     nameof(CloneViewModel.WriteStatusText),
                     nameof(CloneViewModel.HasWriteReport),
                     nameof(CloneViewModel.FileLine),
                     nameof(CloneViewModel.HasFile),
                     // Both presses bind their own gate, so both have to be
                     // announced or a button stays grey after the run ends.
                     nameof(CloneViewModel.CanRead),
                     nameof(CloneViewModel.CanWrite),
                     // D12's press binds its own gate too.
                     nameof(CloneViewModel.CanStore),
                     nameof(CloneViewModel.IsBusy),
                     nameof(CloneViewModel.ReadGateReason),
                     nameof(CloneViewModel.HasReadGateReason),
                     nameof(CloneViewModel.WriteGateReason),
                     nameof(CloneViewModel.HasWriteGateReason),
                     nameof(CloneViewModel.OpenFileNotice),
                     nameof(CloneViewModel.ShowsOpenFileNotice),
                     nameof(CloneViewModel.ShowsOpenFileError),
                     nameof(CloneViewModel.ReadFileNotice),
                     nameof(CloneViewModel.ShowsReadFileNotice),
                     nameof(CloneViewModel.ShowsReadFileError),
                 })
            Assert.Contains(name, raised);

        // …and the READ tab's status, which only a read moves.
        raised.Clear();
        await RealReadAsync(vm, clone, session, radio);
        Assert.Contains(nameof(CloneViewModel.ReadStatusText), raised);
        Assert.Contains(nameof(CloneViewModel.HasReadReport), raised);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Shrink every wait a campaign can sit in, so a run against a
    /// transport that answers NOTHING finishes in milliseconds instead of
    /// waiting out the field budgets.</summary>
    private static void Impatient(CloneService clone, Prc138Radio radio)
    {
        radio.ModeChangeTimeoutMs = 20;
        radio.Ale.RefreshTimeoutMs = 20;
        clone.SentinelTimeoutMs = 20;
        clone.ReadCompletionTimeoutMs = 20;
        clone.GateTimeoutMs = 20;
        clone.ChannelDumpTimeoutMs = 20;
        clone.ChannelDumpQuietMs = 1;
        clone.ChannelDumpPollMs = 1;
        clone.ReadPollMs = 1;
        clone.AnalogSquelchSettleMs = 1;
        clone.ZeroizeSettleTimeoutMs = 1;
    }

    /// <summary>
    /// A REAL read that stops at its first leg: the session is dropped the
    /// moment the campaign is under way, so every leg bails at its own phase
    /// check. The service still INSTALLS the partial file it captured, which is
    /// the state the identity rules are about.
    ///
    /// <para>The drop is issued from the service's OWN <c>Changed</c> event —
    /// the campaign's thread — and not from the test thread. Dropping it from
    /// here would race the campaign's continuations: the phase handler walks
    /// the file's collections while the campaign is still filling them, and the
    /// enumeration throws. In the app both sides marshal to the UI thread, so
    /// that race is the harness's to avoid, not the VM's to survive.</para>
    /// </summary>
    private static async Task RealReadAsync(
        CloneViewModel vm, CloneService clone, RadioSession session, Prc138Radio radio)
    {
        Impatient(clone, radio);

        void DropOnceRunning(object? sender, EventArgs e)
        {
            if (!clone.IsRunning) return;
            clone.Changed -= DropOnceRunning;
            session.Close();
        }

        clone.Changed += DropOnceRunning;
        try
        {
            await vm.ReadCommand.ExecuteAsync(null);
        }
        finally
        {
            clone.Changed -= DropOnceRunning;
        }
    }

    /// <summary>A read the SERVICE's gate closes: the card's gate was open when
    /// the press was handled and the service's has shut by the time it is
    /// asked. <c>CloneService.ReadAsync</c> returns without installing a file,
    /// so the file in hand — and its identity — are exactly as they were. The
    /// race is made deterministic by dropping the session on the busy
    /// notification the command's own start raises.</summary>
    private static async Task GateClosedReadAsync(
        CloneViewModel vm, CloneService clone, RadioSession session, Prc138Radio radio)
    {
        void CloseOnFirstBusyRaise(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CloneViewModel.IsBusy)) return;
            vm.PropertyChanged -= CloseOnFirstBusyRaise;
            session.Close();
        }

        Impatient(clone, radio);
        vm.PropertyChanged += CloseOnFirstBusyRaise;
        try
        {
            await vm.ReadCommand.ExecuteAsync(null);
        }
        finally
        {
            vm.PropertyChanged -= CloseOnFirstBusyRaise;
        }
    }

    /// <summary>A REAL write that stops at leg 2: the wipe goes out and the
    /// settle window is one millisecond, so the campaign stops there with the
    /// radio wiped — through exactly the status/report route a whole write
    /// takes.</summary>
    private static async Task RealWriteAsync(
        CloneViewModel vm, CloneService clone, Prc138Radio radio, FakeConfirmationPrompt prompt)
    {
        Impatient(clone, radio);
        prompt.EnqueueAnswer(true);
        await vm.WriteCommand.ExecuteAsync(null);
    }

    private static void ConnectReady(RadioSession session, InjectingTransport transport)
    {
        session.Connect(new Falcon.Core.Transport.PortSettings { PortName = "TEST" });
        transport.InjectLine("PORT_REMOTE ECHO OFF");
        transport.InjectLine("Battery Status FULL 29.7V");
        transport.InjectLine("SSB>");
        Assert.Equal(SessionPhase.Ready, session.Phase);
    }

    private static void Inject(InjectingTransport transport, string line) => transport.InjectLine(line);
}
