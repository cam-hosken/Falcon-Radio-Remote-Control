using System.Reflection;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 15 H-2 — <b>the affirmative is on the left.</b> The owner's ask
/// ("in general the 'on' setting is on the LEFT") is a display RULE, and a
/// rule that lives only in prose gets undone by the next builder someone
/// copies. This guard reads every choice list the settings VMs BUILD and
/// asserts the ON-state comes first.
///
/// <para><b>Scope</b> (plan §18.2 H-2, critic F63): the VM-built
/// <c>IReadOnlyList&lt;ChoiceItem&gt;</c> properties on the five settings
/// VMs — <see cref="SsbSettingsViewModel"/>, <see cref="HopSettingsViewModel"/>,
/// <see cref="AleSettingsViewModel"/>, <see cref="DeviceSettingsViewModel"/>
/// and <see cref="ModemPresetsViewModel"/> — enumerated by REFLECTION so a
/// list added later is policed the day it appears. The inline XAML On/Off
/// rows of the ALE settings and SSB Operate panes are already
/// affirmative-first and sit OUTSIDE this guard: reflection cannot see them,
/// and a markup scan for them is a different instrument. That exclusion is
/// documented in <c>docs/ui.md</c>'s constitution rule, not hidden here.</para>
///
/// <para><b>The rule.</b> For a list whose VALUES are exactly one of the
/// recognised pairs — {On,Off}, {Enable,Disable}, {Enable,Bypass}, {Yes,No},
/// {Enabled,Disabled} — the affirmative member is at index 0. Lists that are
/// not one of those pairs (bandwidths, tones, modem types, OFF/MOM …) rank
/// nothing and are left alone. <see cref="SsbSettingsViewModel.AntennaChoices"/>
/// is the one NAMED exception: it has no "on", so plan H-D1 rules
/// <c>Auto</c> first as an ARCHITECT DEFAULT (the manual's
/// <c>BNc/AUto/TUned</c> ranks nothing — <c>docs/protocol.md</c>). If the
/// owner ever keeps <c>BNC · Auto · Tuned</c>, delete the Antenna clause and
/// the rest of the rule stands.</para>
///
/// <para><b>Anti-vacuity.</b> A reflection guard that finds nothing passes
/// loudly, so the floor is asserted first: at least eight populated lists
/// SEEN, and the four the round actually reorders named individually (critic
/// F64). A VM whose constructor stops populating its lists fails here rather
/// than silently retiring the rule.</para>
/// </summary>
public class ChoiceOrderGuardTests : SessionTestBase
{
    /// <summary>The affirmative-first pairs, as VALUE SET → the member that
    /// must be index 0. Compared case-insensitively; a list matches only when
    /// its values are exactly the pair.</summary>
    private static readonly (string[] Pair, string Affirmative)[] Pairs =
    [
        (["On", "Off"], "On"),
        (["Enable", "Disable"], "Enable"),
        (["Enable", "Bypass"], "Enable"),
        (["Yes", "No"], "Yes"),
        (["Enabled", "Disabled"], "Enabled"),
    ];

    private const string AntennaList = "SsbSettingsViewModel.AntennaChoices";

    /// <summary>The four lists this round reorders — the anti-vacuity names
    /// (plan §18.3 gate, critic F64).</summary>
    private static readonly string[] PolicedByName =
    [
        "SsbSettingsViewModel.PreampChoices",
        "SsbSettingsViewModel.InternalCouplerChoices",
        "HopSettingsViewModel.InternalCouplerChoices",
        AntennaList,
    ];

    [Fact]
    public void TheAffirmative_IsFirst_OnEverySettingsChoiceList()
    {
        var lists = ReadSettingsChoiceLists();

        // Anti-vacuity FIRST: the rule below means nothing if the reflection
        // walk came back empty-handed.
        Assert.True(lists.Count >= 8,
            $"the guard saw only {lists.Count} populated choice lists — it must see at least 8: "
            + string.Join(", ", lists.Keys));
        foreach (string name in PolicedByName)
            Assert.True(lists.ContainsKey(name),
                $"{name} was not seen by the guard — it must police that list. Seen: "
                + string.Join(", ", lists.Keys));

        var offenders = new List<string>();

        foreach ((string name, var values) in lists)
        {
            string rendered = string.Join("·", values);

            if (name == AntennaList)
            {
                // H-D1: no "on" exists here — Auto is the architect default.
                if (!string.Equals(values[0], "Auto", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}: reads {rendered} — Auto must be first (H-D1)");
                continue;
            }

            foreach ((string[] pair, string affirmative) in Pairs)
            {
                if (!IsExactly(values, pair)) continue;
                if (!string.Equals(values[0], affirmative, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}: reads {rendered} — {affirmative} must be first");
                break;
            }
        }

        Assert.True(offenders.Count == 0,
            "the affirmative must be on the LEFT:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The §18.3 gate's FOUR lists, by name and by content — the
    /// by-name half of the anti-vacuity floor, stated so a reader of the
    /// failure knows exactly which orders the round changed.</summary>
    [Fact]
    public void TheFourReorderedLists_ReadTheirNewOrder()
    {
        var lists = ReadSettingsChoiceLists();

        Assert.Equal(["Enable", "Bypass"], lists["SsbSettingsViewModel.PreampChoices"]);
        Assert.Equal(["Enable", "Bypass"], lists["SsbSettingsViewModel.InternalCouplerChoices"]);
        Assert.Equal(["Enable", "Bypass"], lists["HopSettingsViewModel.InternalCouplerChoices"]);
        Assert.Equal(["Auto", "BNC", "Tuned"], lists[AntennaList]);
    }

    // ---- the reflection walk ----------------------------------------------

    /// <summary>Every populated <c>IReadOnlyList&lt;ChoiceItem&gt;</c>
    /// property on the five settings VMs, keyed <c>TypeName.PropertyName</c>.
    /// The VMs are built bare: every builder runs from the constructor's own
    /// <c>Refresh</c>, and an unconfirmed mirror changes which item is LIT,
    /// never the ORDER — which is the only thing this guard reads.</summary>
    private Dictionary<string, string[]> ReadSettingsChoiceLists()
    {
        object[] vms =
        [
            new SsbSettingsViewModel(new SsbSurface(Radio), Session),
            new HopSettingsViewModel(new HopSurface(Radio), Session, new FakeConfirmationPrompt()),
            new AleSettingsViewModel(new AleSurface(Radio), Session),
            new DeviceSettingsViewModel(new DeviceSurface(Radio), Session, new TestTime()),
            new ModemPresetsViewModel(new ModemSurface(Radio), Session),
        ];

        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (object vm in vms)
        {
            var type = vm.GetType();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(IReadOnlyList<ChoiceItem>)) continue;
                if (property.GetIndexParameters().Length != 0) continue;

                if (property.GetValue(vm) is not IReadOnlyList<ChoiceItem> list || list.Count == 0)
                    continue;                       // an unpopulated list ranks nothing

                found[$"{type.Name}.{property.Name}"] = [.. list.Select(c => c.Value)];
            }
        }

        return found;
    }

    private static bool IsExactly(string[] values, string[] pair)
        => values.Length == pair.Length
           && values.All(v => pair.Any(p => string.Equals(p, v, StringComparison.OrdinalIgnoreCase)))
           && pair.All(p => values.Any(v => string.Equals(p, v, StringComparison.OrdinalIgnoreCase)));
}
