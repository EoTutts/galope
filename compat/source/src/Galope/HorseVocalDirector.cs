namespace Galope;

internal enum HorseVocalCue
{
    Exertion1, Exertion2, Snort1, Snort2, Snort3, Snort4, Snort5
}

// Running time accumulates only while the hooves are on the ground. A jump
// pauses the clock without making an otherwise long run feel like a new run.
internal sealed class HorseVocalDirector
{
    private readonly Random _random;
    private double _runningSeconds;
    private double _nextCue;
    private int _previousSnort = -1;
    private int _previousExertion = -1;

    internal HorseVocalDirector(Random random)
    {
        _random = random;
        Reset();
    }

    internal void Reset()
    {
        _runningSeconds = 0;
        _nextCue = 12 + _random.NextDouble() * 8;
        _previousSnort = -1;
        _previousExertion = -1;
    }

    internal void Advance(double dt, bool grounded, Action<HorseVocalCue> play)
    {
        if (!grounded || dt <= 0) return;
        _runningSeconds += dt;
        if (_runningSeconds < _nextCue) return;
        _nextCue = _runningSeconds + 8 + _random.NextDouble() * 10;
        if (_random.NextDouble() < 0.5)
        {
            int index = _random.Next(2);
            if (index == _previousExertion) index = 1 - index;
            _previousExertion = index;
            play(index == 0 ? HorseVocalCue.Exertion1 : HorseVocalCue.Exertion2);
        }
        else
        {
            int index = _random.Next(_previousSnort < 0 ? 5 : 4);
            if (_previousSnort >= 0 && index >= _previousSnort) index++;
            _previousSnort = index;
            play((HorseVocalCue)((int)HorseVocalCue.Snort1 + index));
        }
    }
}
