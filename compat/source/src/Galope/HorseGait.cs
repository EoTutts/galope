namespace Galope;

internal enum HoofFoot { HindLeft, HindRight, FrontLeft, FrontRight }
internal readonly record struct HoofEvent(int Speed, HoofFoot Foot, double AccentDb,
    bool StartsCycle = false, bool Primary = true);
internal readonly record struct HoofBeat(double Phase, HoofFoot First, HoofFoot? Second,
    double PairDelay, double AccentDb);

// Timing and foot order adapted from Riveria's horse cadence reference.
// The clock follows travelled distance through the five ordinary speeds.
// Beyond speed five its cadence grows more slowly, keeping distinct hoofbeats.
internal sealed class HorseGait
{
    private sealed record Pattern(double CycleSeconds, HoofBeat[] Beats);
    private static readonly Pattern[] Patterns = [
        new(0, []),
        new(1.188280, [
            new(0, HoofFoot.HindLeft, null, 0, -0.60),
            new(0.18 / 1.22, HoofFoot.FrontLeft, null, 0, 0),
            new(0.52 / 1.22, HoofFoot.HindRight, null, 0, -0.40),
            new(0.70 / 1.22, HoofFoot.FrontRight, null, 0, 0.50)]),
        new(0.983740, [
            new(0, HoofFoot.HindLeft, null, 0, -0.45),
            new(0.14 / 1.01, HoofFoot.FrontLeft, null, 0, 0.10),
            new(0.40 / 1.01, HoofFoot.HindRight, null, 0, -0.30),
            new(0.54 / 1.01, HoofFoot.FrontRight, null, 0, 0.55)]),
        new(0.584400, [
            new(0, HoofFoot.HindLeft, HoofFoot.FrontRight, 0.045, -0.20),
            new(0.5, HoofFoot.HindRight, HoofFoot.FrontLeft, 0.045, 0.35)]),
        new(0.506480, [
            new(0, HoofFoot.HindRight, null, 0, -0.55),
            new(0.12 / 0.52, HoofFoot.HindLeft, HoofFoot.FrontRight, 0.022, -0.15),
            new(0.24 / 0.52, HoofFoot.FrontLeft, null, 0, 0.55)]),
        new(0.487000, [
            new(0, HoofFoot.HindRight, null, 0, -0.80),
            new(0.09 / 0.50, HoofFoot.HindLeft, null, 0, -0.35),
            new(0.16 / 0.50, HoofFoot.FrontRight, null, 0, 0),
            new(0.27 / 0.50, HoofFoot.FrontLeft, null, 0, 0.65)])
    ];

    private readonly Random _random;
    private readonly double[] _worldSpeed;
    private readonly List<(double Remaining, HoofEvent Event)> _pairedFeet = [];
    private int _activeSpeed = 1;
    private int _nextBeat;
    private double _phase;
    private double _cycleDistance;
    internal double Phase => _phase;
    internal int ActiveSpeed => _activeSpeed;

    internal static double ContactPhase(int speed, HoofFoot foot)
    {
        Pattern pattern = Patterns[speed];
        foreach (HoofBeat beat in pattern.Beats)
        {
            if (beat.First == foot) return beat.Phase;
            if (beat.Second == foot) return beat.Phase + beat.PairDelay / pattern.CycleSeconds;
        }
        return 0;
    }

    internal HorseGait(Random random, double[] worldSpeed)
    {
        _random = random;
        _worldSpeed = worldSpeed;
        Reset(1);
    }

    internal void Reset(int speed)
    {
        _activeSpeed = speed;
        _phase = 0;
        _nextBeat = 0;
        _pairedFeet.Clear();
        BeginCycle();
    }

    private void BeginCycle()
    {
        double variation = 0.975 + _random.NextDouble() * 0.05;
        _cycleDistance = _worldSpeed[_activeSpeed] * Patterns[_activeSpeed].CycleSeconds * variation;
    }

    internal void Advance(double distance, int requestedSpeed, double dt, Action<HoofEvent> play)
    {
        // The 5+ runner can travel almost twice as fast as a horse at pace 5.
        // Let the gallop quicken slightly without squeezing four recordings
        // into the same instant and masking their individual hoof strikes.
        if (_activeSpeed == 5 && dt > 0 && distance > 42 * dt)
            distance = (42 + (distance / dt - 42) * 0.25) * dt;
        for (int i = _pairedFeet.Count - 1; i >= 0; i--)
        {
            var pending = _pairedFeet[i];
            double remaining = pending.Remaining - dt;
            if (remaining <= 0)
            {
                play(pending.Event);
                _pairedFeet.RemoveAt(i);
            }
            else _pairedFeet[i] = (remaining, pending.Event);
        }

        while (distance > 0)
        {
            double step = Math.Min(distance, (1 - _phase) * _cycleDistance);
            double endPhase = _phase + step / _cycleDistance;
            HoofBeat[] beats = Patterns[_activeSpeed].Beats;
            while (_nextBeat < beats.Length && beats[_nextBeat].Phase <= endPhase + 1e-10)
            {
                bool startsCycle = _nextBeat == 0;
                HoofBeat beat = beats[_nextBeat++];
                double pairAccent = beat.Second is null ? 0 : -1.8;
                play(new HoofEvent(_activeSpeed, beat.First, beat.AccentDb + pairAccent,
                    startsCycle));
                if (beat.Second is HoofFoot second)
                    _pairedFeet.Add((beat.PairDelay,
                        new HoofEvent(_activeSpeed, second, beat.AccentDb + pairAccent,
                            false, false)));
            }
            distance -= step;
            _phase = endPhase;
            if (_phase >= 1 - 1e-10)
            {
                _phase = 0;
                _nextBeat = 0;
                _activeSpeed = requestedSpeed;
                BeginCycle();
            }
        }
    }
}
