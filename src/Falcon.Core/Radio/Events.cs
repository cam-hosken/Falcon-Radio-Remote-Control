namespace Falcon.Core.Radio;

public enum ConnectionState
{
    Disconnected,
    /// <summary>Port open, connect ritual in flight — UI stays locked.</summary>
    Initializing,
    /// <summary>Sentinel answered: every init command was processed by the radio.</summary>
    Ready,
    /// <summary>Port opened but the radio never answered within the watchdog window.</summary>
    Failed,
}

public sealed class RadioStateChangedEventArgs(RadioProperty property) : EventArgs
{
    public RadioProperty PropertyChanged { get; } = property;
}

public sealed class MessageReceivedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class LineSentEventArgs(string line) : EventArgs
{
    public string Line { get; } = line;
}

public sealed class RadioErrorEventArgs(string message, string? line) : EventArgs
{
    /// <summary>Human-readable description of the problem.</summary>
    public string Message { get; } = message;
    /// <summary>The raw response line that triggered it (may be null).</summary>
    public string? Line { get; } = line;
}

/// <summary>
/// Raised whenever the Core sends commands the operator did not directly
/// cause (trigger-table re-polls, the FM-squelch cycle). Principle #4
/// (plan §0): no silent writes — the app layer surfaces these in the
/// Console log.
/// </summary>
public sealed class CompensationAppliedEventArgs(string reason, IReadOnlyList<string> commands) : EventArgs
{
    public string Reason { get; } = reason;
    public IReadOnlyList<string> Commands { get; } = commands;
}
