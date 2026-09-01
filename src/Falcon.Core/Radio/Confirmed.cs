namespace Falcon.Core.Radio;

/// <summary>
/// A radio-reported value with its confirmation status. Every displayed value
/// comes only from lines the radio actually sent THIS session (plan §0):
/// until the radio reports it, the value is Unconfirmed and consumers must
/// render "—", never a default. This is the structural fix for the old repo's
/// twice-shipped enum-default leak (ordinal 0 == On/USB/Yes).
/// </summary>
public readonly struct Confirmed<T>
{
    private Confirmed(T value) { Value = value; IsConfirmed = true; }

    /// <summary>True once the radio has reported this value this session.</summary>
    public bool IsConfirmed { get; }

    /// <summary>The reported value. Meaningless (default) when unconfirmed —
    /// consumers must check <see cref="IsConfirmed"/> first.</summary>
    public T? Value { get; }

    public static Confirmed<T> Of(T value) => new(value);
    public static Confirmed<T> Unconfirmed => default;

    public override string ToString() => IsConfirmed ? $"{Value}" : "—";
}
