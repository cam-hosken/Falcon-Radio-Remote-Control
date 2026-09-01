using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Operating-mode slice: reported mode (Confirmed — the spine
/// highlight comes ONLY from this), the 30 s mode-change-pending flag, and
/// the select intent.</summary>
public sealed class ModeSurface : RadioSurface
{
    /// <summary>Round 14 C: the coupler CONVERGENCE policy
    /// (plan/plan-round14.md §4-C, owner ruling R10). OPTIONAL for the same
    /// reason as on <c>HopSurface</c> — the app's composition always supplies
    /// it, and the compositions that do not are the clone/bench stacks, which
    /// must drive the RAW <see cref="Select"/>.</summary>
    private readonly CouplerPolicy? _coupler;

    public ModeSurface(Prc138Radio radio, CouplerPolicy? coupler = null)
        : base(radio, RadioProperty.OperatingMode, RadioProperty.ModeChangePending)
        => _coupler = coupler;

    public Confirmed<OperatingMode> Mode => Radio.State.OperatingMode;

    /// <summary>True between SelectMode and the new mode's prompt (or the
    /// 30 s deadline) — the segmented control's busy state.</summary>
    public bool IsChangePending => Radio.IsModeChangePending;

    /// <summary>Sends ONLY the mode command (Q4: no switch-driven re-reads).
    /// <para>The RAW intent, and it stays raw: the clone campaign's mode
    /// transitions call THIS, and constitution §3.3 says the clone paths never
    /// route through the coupler policy.</para></summary>
    public void Select(OperatingMode mode) => Radio.SelectMode(mode);

    /// <summary>ROUND 14 C — the OPERATOR's route to a mode change
    /// (plan/plan-round14.md §4-C, owner ruling R10), and its ONLY caller is
    /// <c>ModeViewModel.SelectMode</c>. Asks the coupler convergence policy
    /// first, then delegates to <see cref="Select"/>, so the wire reads
    /// <c>INTCOUPLER …</c> then the mode command.
    ///
    /// <para><b>The order is the point when ENTERING HOP.</b> Mode entry
    /// REGENERATES the current net's hopset (docs/protocol.md, both P-1 runs),
    /// so the coupler has to be right before <c>HO</c> goes out or a WB
    /// current net regenerates straight into <c>WB_Invalid</c>.</para>
    ///
    /// <para>With no policy in this composition this is exactly
    /// <see cref="Select"/>.</para></summary>
    public void SelectAsOperatorGesture(OperatingMode mode)
    {
        _coupler?.OnModeSelect(mode);
        Select(mode);
    }
}
