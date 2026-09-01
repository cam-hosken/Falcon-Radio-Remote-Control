namespace Falcon.App.Behaviors;

/// <summary>
/// Round 7 (DB, owner): "typing into any field should erase whatever is in
/// there and let you type immediately." On the programming surfaces that is
/// solved structurally — reported values are placeholders, so the boxes are
/// empty — but fields that HOLD typed text across uses (RF gain, RWAS key,
/// the LIST add box) get this behavior instead: focusing selects the whole
/// content, so the first keystroke replaces it.
/// </summary>
public sealed class SelectAllOnFocusBehavior : Behavior<Entry>
{
    protected override void OnAttachedTo(Entry entry)
    {
        base.OnAttachedTo(entry);
        entry.Focused += OnFocused;
    }

    protected override void OnDetachingFrom(Entry entry)
    {
        entry.Focused -= OnFocused;
        base.OnDetachingFrom(entry);
    }

    private static void OnFocused(object? sender, FocusEventArgs e)
    {
        if (sender is not Entry entry || string.IsNullOrEmpty(entry.Text)) return;
        // Deferred one tick: some platforms reset the selection AFTER the
        // Focused event as part of placing the caret.
        entry.Dispatcher.Dispatch(() =>
        {
            entry.CursorPosition = 0;
            entry.SelectionLength = entry.Text?.Length ?? 0;
        });
    }
}
