using Microsoft.Maui.Handlers;

namespace Falcon.App.Controls;

/// <summary>
/// D18(b) (plan-clone-write-structural.md §2, 2026-08-30; owner: "we should be
/// able to highlight and copy console output text").
///
/// <para><b>What it is.</b> A <see cref="Label"/> that does nothing different
/// in MAUI and everything different on the platform: the native text view
/// behind it is put into SELECTABLE mode, so the operator can drag a selection
/// across a console line and copy it with the platform's own gesture and
/// context menu. MAUI has no cross-platform "selectable label", which is why
/// this is a handler mapping rather than a property.</para>
///
/// <para><b>Why a SUBCLASS — this is the whole scoping mechanism.</b> The
/// mapping below is appended to <c>LabelHandler.Mapper</c>, which is shared by
/// EVERY label in the app, and its body opens with a type test: only a
/// <see cref="ConsoleLogLabel"/> is touched, and every other label leaves the
/// mapping exactly as it entered. Making the whole app's text selectable would
/// change the meaning of a tap-and-hold on every heading and every value in
/// the GUI, which is not what was asked for and is not something a Label style
/// can be scoped back out of.</para>
///
/// <para><b>Styling is untouched.</b> The subclass adds no properties and sets
/// none; the console line's Consolas/12 typography, its wrapping and its
/// column live in the DataTemplate exactly as before.</para>
///
/// <para><b>The bench owns the UX verdict</b> (docs/bench-checklist.md): that a
/// drag really selects on the Android recycler, and that the selection is not
/// stolen by the log's own scroll-to-end while lines are arriving (Pause is the
/// operator's answer to that). If the recycler defeats it on the bench, D18
/// records the fallback — a select-mode toggle swapping in a read-only editor
/// of the visible text — and that fallback is deliberately NOT built
/// alongside this.</para>
/// </summary>
public class ConsoleLogLabel : Label
{
    /// <summary>The mapping key — unique per D18, so a later mapping cannot
    /// silently replace this one.</summary>
    private const string MappingKey = "Falcon.ConsoleLogLabel.SelectableText";

    /// <summary>Called ONCE from <c>MauiProgram.CreateMauiApp</c>. Idempotent
    /// by the mapper's own semantics (the same key replaces rather than
    /// stacks), and safe on any platform: where neither branch compiles, the
    /// mapping is a no-op and the label is an ordinary label.</summary>
    public static void EnableTextSelection()
    {
        LabelHandler.Mapper.AppendToMapping(MappingKey, (handler, view) =>
        {
            // THE SCOPE. Every Label in the app arrives here; only ours leaves
            // changed.
            if (view is not ConsoleLogLabel) return;
#if ANDROID
            handler.PlatformView.SetTextIsSelectable(true);
#elif WINDOWS
            handler.PlatformView.IsTextSelectionEnabled = true;
#endif
        });
    }
}
