namespace Falcon.App.Core.Services;

/// <summary>
/// The GUI-owned confirmation seam (UI tweaks round 10 §5, owner rulings 8
/// and 9).
///
/// <para><b>Why it exists.</b> Confirmation for the app's destructive-DATA
/// gestures used to live in Core as typed-token parameters. Owner ruling 9
/// moved it: "the back end does what the GUI tells it" — Core executes, and
/// asking the operator is a GUI concern. A ViewModel must still never touch
/// MAUI UI directly (invariant 2), so the asking goes through this seam: the
/// MAUI implementation lives in the app head (<c>Falcon.App/Services</c>), the
/// ViewModels take the interface, and the tests take a controllable fake.</para>
///
/// <para><b>Scope.</b> This is for the two named destructive-DATA senders —
/// the ALE address-book ERASE and the radio baud change — plus the per-caller
/// gestures in the §5 prompt table (delete address, delete self, erase book,
/// clear HOP net). It is NOT the mechanism for the three TRANSMIT-hazard
/// gates (<c>SetKeyline</c> TRANSMIT / <c>SelfTest</c> / <c>VswrTest</c>),
/// which keep their Core token gates unchanged.</para>
///
/// <para><b>Contract.</b> Exactly one method: a two-button question. The
/// returned task completes <see langword="true"/> when the operator chose the
/// <c>accept</c> button and <see langword="false"/> when they chose
/// <c>cancel</c> or dismissed the prompt. Callers follow the §5
/// lifecycle contract: capture the target at PRESS, re-check the send gate
/// after the await, send once against the captured target on accept, send
/// nothing on cancel / a faulted or cancelled task, and re-prompt on every
/// press.</para>
/// </summary>
public interface IConfirmationPrompt
{
    /// <summary>Ask a two-button question and wait for the answer.</summary>
    /// <param name="title">Short question naming the target.</param>
    /// <param name="message">What the radio will actually do.</param>
    /// <param name="accept">The destructive button's word (e.g. "Delete").</param>
    /// <param name="cancel">The safe button's word (e.g. "Cancel").</param>
    /// <returns>True when the operator accepted; false otherwise.</returns>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}
