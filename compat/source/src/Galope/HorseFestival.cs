namespace Galope;

internal enum FestivalPhase { Waiting, Warning, Active }
internal enum FestivalChange { None, Warning, Started, Ended }

// Warning and active phases temporarily target pace two. Pausing the game
// also pauses this clock.
internal sealed class HorseFestival
{
    private readonly Random _random;
    private readonly double? _firstDelay;
    private double _remaining;
    private double _activeDuration;
    internal FestivalPhase Phase { get; private set; }
    internal bool CanScheduleDog => Phase == FestivalPhase.Waiting && _remaining > 0;
    internal double Traversal => Phase switch
    {
        FestivalPhase.Warning => Math.Clamp(0.5 * (4 - _remaining) / 4, 0, 0.5),
        FestivalPhase.Active => Math.Clamp(0.5 + 0.5 *
            (_activeDuration - _remaining) / _activeDuration, 0.5, 1),
        _ => 0
    };

    internal HorseFestival(Random random, double? firstDelay = null)
    {
        _random = random;
        _firstDelay = firstDelay;
        Reset();
    }

    internal void Reset()
    {
        Phase = FestivalPhase.Waiting;
        _remaining = _firstDelay ?? 28 + _random.NextDouble() * 22;
        _activeDuration = 0;
    }

    internal FestivalChange Advance(double dt, bool allowWarning = true)
    {
        if (dt <= 0) return FestivalChange.None;
        _remaining -= dt;
        if (_remaining > 0) return FestivalChange.None;
        if (Phase == FestivalPhase.Waiting && !allowWarning)
        {
            _remaining = 0;
            return FestivalChange.None;
        }
        switch (Phase)
        {
            case FestivalPhase.Waiting:
                Phase = FestivalPhase.Warning;
                _remaining = 4;
                _activeDuration = 13 + _random.NextDouble() * 7;
                return FestivalChange.Warning;
            case FestivalPhase.Warning:
                Phase = FestivalPhase.Active;
                _remaining = _activeDuration;
                return FestivalChange.Started;
            default:
                Phase = FestivalPhase.Waiting;
                _remaining = 38 + _random.NextDouble() * 27;
                return FestivalChange.Ended;
        }
    }
}
