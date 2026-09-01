namespace Falcon.App.Core.ViewModels;

/// <summary>
/// VM-layer clamp for repeat-fire inputs (plan §2.4): a held chevron /
/// arrow key must not queue unbounded commands behind the
/// prompt-gated transport. Fires pass or are DROPPED — never queued; a
/// dropped repeat is recomputed from confirmed state on the next attempt,
/// so nothing stale is ever sent late.
/// </summary>
public sealed class RepeatRateLimiter(TimeProvider time, TimeSpan interval)
{
    private DateTimeOffset _lastFire = DateTimeOffset.MinValue;

    /// <summary>True (and starts a new interval) if enough time has passed
    /// since the last accepted fire; false = drop the repeat.</summary>
    public bool TryFire()
    {
        var now = time.GetUtcNow();
        if (now - _lastFire < interval) return false;
        _lastFire = now;
        return true;
    }
}
