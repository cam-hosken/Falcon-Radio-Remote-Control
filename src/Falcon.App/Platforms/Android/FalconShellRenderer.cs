using System.Runtime.CompilerServices;
using Google.Android.Material.BottomNavigation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

// Aliased through global:: on purpose: this file's own namespace ENDS in
// "Android", so a bare `using Android.Widget;` would be resolved relative to
// Falcon.App.Platforms.Android and fail to find the platform namespace.
using AView = global::Android.Views.View;
using AViewGroup = global::Android.Views.ViewGroup;
using ATextView = global::Android.Widget.TextView;
using AGravityFlags = global::Android.Views.GravityFlags;
using AFrameLayout = global::Android.Widget.FrameLayout;

namespace Falcon.App.Platforms.Android;

/// <summary>
/// GUI rejigger N6: the Android bottom-tab labels sat at the BOTTOM of the
/// bar, crowding the system navigation buttons. N6 nudged the item content
/// upward with Material's item padding (top 4 dp, bottom 16 dp) — those two
/// values are still applied here and are the BASELINE this file starts from.
///
/// <para><b>UI tweaks round 6 (CA) — move the label in LAYOUT, not in
/// paint.</b> Round 5 lifted the label TextViews with <c>TranslationY</c>;
/// on the bench phone the label rose but its TOP was CLIPPED — a translation
/// is a draw-time offset, the view still occupies its original layout slot,
/// and Android parents clip children to their bounds by default, so the
/// label was painted partly outside Material's labels-group box. The fix is
/// to move the labels GROUP inside its item frame by adjusting its
/// <c>MarginLayoutParams</c> instead: a margin participates in layout, so
/// the group genuinely owns its new position and nothing can clip. The
/// group is bottom-anchored in Material's item frame, so a BIGGER bottom
/// margin lifts it (a top-anchored variant gets its top margin reduced,
/// clamped at zero). Icons are separate children and are not touched; the
/// bar's height is set by the item frame, not the group's margin, so the
/// bar cannot move.</para>
///
/// <para>The lift distance is half the label's REAL text height, from its
/// own paint at runtime (<c>descent − ascent</c>), never a dp constant.
/// Margins are ASSIGNED as original-plus-lift with the original captured
/// once per view (a weak table), so re-asserting on every layout pass —
/// Material re-lays-out on selection and configuration changes — is
/// idempotent, never accumulating.</para>
///
/// <para><b>Three tiers, most layout-honest first.</b> (1) Margin-lift the
/// <c>navigation_bar_item_labels_group</c> views (id resolved by NAME, so a
/// Material bump can only break the lookup, never the build). (2) If no
/// group id resolves or no view carries margin params, fall back to the
/// round-5 label <c>TranslationY</c> — now with <c>clipChildren=false</c> /
/// <c>clipToPadding=false</c> on the label's ancestors up to the bar, which
/// is what round 5 was missing. (3) If no labels are found at all, the
/// round-5 padding rebalance (top 0, bottom 20 — sum unchanged, the bar
/// cannot move). Whenever tier 1 or 2 runs, item padding is restored to the
/// N6 baseline so two mechanisms are never in effect together.</para>
///
/// <para><b>Device verification is the operator's</b> (Stage 7/8 pattern)
/// and bench A5d stays OPEN. The by-eye tells: label up ~half a text height,
/// top NOT clipped, icons unmoved → tier 1; label up and unclipped but this
/// file's margin path was somehow bypassed → tier 2 behaves identically to
/// the eye; icons moved slightly too → tier 3 fired.</para>
/// </summary>
public class FalconShellRenderer : ShellRenderer
{
    protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem)
        => new TopAlignedLabelTracker(this, shellItem);

    private sealed class TopAlignedLabelTracker(IShellContext shellContext, ShellItem shellItem)
        : ShellBottomNavViewAppearanceTracker(shellContext, shellItem)
    {
        /// <summary>The N6 item padding — the baseline whenever the label
        /// lift (margin or translation) is doing the work.</summary>
        private const int BaselinePaddingTopDp = 4;
        private const int BaselinePaddingBottomDp = 16;

        /// <summary>Tier (3): the round-5 rebalance. Top + bottom is the SAME
        /// 20 dp as the baseline, which is what makes it safe — the item's
        /// content box keeps its height, only the content's position inside it
        /// changes, so the bar cannot move either way.</summary>
        private const int FallbackPaddingTopDp = 0;
        private const int FallbackPaddingBottomDp = 20;

        private const string LabelGroupIdName = "navigation_bar_item_labels_group";

        /// <summary>Original margins, captured the first time a group is
        /// lifted, so re-assertion is original+lift (idempotent), never
        /// current+lift (accumulating). Weak: dies with the view.</summary>
        private static readonly ConditionalWeakTable<AView, StrongBox<(int Top, int Bottom)>> OriginalMargins = new();

        private bool _hooked;

        public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
        {
            base.SetAppearance(bottomView, appearance);
            Apply(bottomView);
        }

        public override void ResetAppearance(BottomNavigationView bottomView)
        {
            base.ResetAppearance(bottomView);
            Apply(bottomView);
        }

        private void Apply(BottomNavigationView bottomView)
        {
            if (!_hooked)
            {
                _hooked = true;
                bottomView.LayoutChange += (_, _) => RaiseLabels(bottomView);
            }
            RaiseLabels(bottomView);
        }

        private static void RaiseLabels(BottomNavigationView bottomView)
        {
            var groups = FindLabelGroups(bottomView);

            // Tier 1 — layout-honest: lift each labels group by margin.
            bool anyLifted = false;
            foreach (var group in groups)
                anyLifted |= TryMarginLift(group);

            if (anyLifted)
            {
                SetItemPadding(bottomView, BaselinePaddingTopDp, BaselinePaddingBottomDp);
                return;
            }

            // Tier 2 — draw-time lift with clipping disabled on the way up.
            var labels = new List<ATextView>();
            Collect(bottomView, groupId: 0, insideGroup: false, labels);
            if (labels.Count > 0)
            {
                SetItemPadding(bottomView, BaselinePaddingTopDp, BaselinePaddingBottomDp);
                foreach (var label in labels)
                {
                    float lift = TextHeight(label) / 2f;
                    if (lift <= 0) continue;
                    label.TranslationY = -lift;             // assignment, not accumulation
                    UnclipAncestors(label, bottomView);
                }
                return;
            }

            // Tier 3 — nothing to lift; rebalance the padding instead.
            SetItemPadding(bottomView, FallbackPaddingTopDp, FallbackPaddingBottomDp);
        }

        /// <summary>Lift one labels group within its item frame via margins.
        /// Returns false when the group has no margin-capable params (tier 2
        /// takes over).</summary>
        private static bool TryMarginLift(AViewGroup group)
        {
            if (group.LayoutParameters is not AViewGroup.MarginLayoutParams margins) return false;

            float lift = TextHeight(FirstTextView(group));
            if (lift <= 0) return false;                    // nothing measured yet
            int liftPx = (int)(lift / 2f);

            var original = OriginalMargins.GetValue(group,
                _ => new StrongBox<(int, int)>((margins.TopMargin, margins.BottomMargin))).Value;

            // Bottom-anchored (Material's shape): a bigger bottom margin lifts
            // the group. Top-anchored: a smaller top margin does, clamped ≥ 0.
            bool bottomAnchored = group.LayoutParameters is not AFrameLayout.LayoutParams frame
                || ((AGravityFlags)frame.Gravity & AGravityFlags.Bottom) == AGravityFlags.Bottom;

            if (bottomAnchored)
            {
                margins.TopMargin = original.Top;
                margins.BottomMargin = original.Bottom + liftPx;
            }
            else
            {
                margins.TopMargin = Math.Max(0, original.Top - liftPx);
                margins.BottomMargin = original.Bottom;
            }
            group.LayoutParameters = margins;               // triggers a re-layout with the new slot
            return true;
        }

        private static ATextView? FirstTextView(AView view)
        {
            if (view is ATextView text) return text;
            if (view is not AViewGroup group) return null;
            for (int i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is { } child && FirstTextView(child) is { } found)
                    return found;
            return null;
        }

        /// <summary>The label's REAL text height for the size and font in
        /// force, from its own paint — not a dp constant. Zero when there is
        /// no label to measure.</summary>
        private static float TextHeight(ATextView? label)
        {
            if (label is null) return 0f;
            var paint = label.Paint;
            return paint is not null ? paint.Descent() - paint.Ascent() : label.TextSize;
        }

        /// <summary>Tier 2's missing half from round 5: a translated view is
        /// clipped by every ancestor whose bounds it leaves, so clipping is
        /// disabled from the label's parent up to (and including) the bar.</summary>
        private static void UnclipAncestors(AView view, BottomNavigationView bar)
        {
            var parent = view.Parent;
            while (parent is AViewGroup group)
            {
                group.SetClipChildren(false);
                group.SetClipToPadding(false);
                if (ReferenceEquals(group, bar)) break;
                parent = group.Parent;
            }
        }

        private static List<AViewGroup> FindLabelGroups(BottomNavigationView bottomView)
        {
            var groups = new List<AViewGroup>();
            var context = bottomView.Context;
            int groupId = context?.Resources?.GetIdentifier(LabelGroupIdName, "id", context.PackageName) ?? 0;
            if (groupId != 0) CollectGroups(bottomView, groupId, groups);
            return groups;
        }

        private static void CollectGroups(AView view, int groupId, List<AViewGroup> groups)
        {
            if (view is not AViewGroup group) return;
            if (group.Id == groupId) { groups.Add(group); return; }
            for (int i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is { } child)
                    CollectGroups(child, groupId, groups);
        }

        /// <summary>Depth-first collect of label TextViews (tier 2) — inside a
        /// BottomNavigationView the only TextViews are the small and large
        /// labels (badges are a BadgeDrawable, not a view).</summary>
        private static void Collect(AView view, int groupId, bool insideGroup, List<ATextView> labels)
        {
            bool inGroup = insideGroup || (groupId != 0 && view.Id == groupId);

            if (view is ATextView text)
            {
                if (groupId == 0 || inGroup) labels.Add(text);
                return;
            }

            if (view is not AViewGroup group) return;
            for (int i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is { } child)
                    Collect(child, groupId, inGroup, labels);
        }

        private static void SetItemPadding(BottomNavigationView bottomView, int topDp, int bottomDp)
        {
            var density = bottomView.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
            bottomView.ItemPaddingTop = (int)(topDp * density);
            bottomView.ItemPaddingBottom = (int)(bottomDp * density);
        }
    }
}
