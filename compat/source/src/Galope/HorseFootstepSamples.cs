namespace Galope;

// A shuffled bag uses every recording before repeating one. The first sample
// of a new bag differs from the last sample of the preceding bag.
internal sealed class HorseFootstepSamples
{
    private readonly Random _random;
    private readonly int[] _order = [0, 1, 2, 3];
    private int _next = 4;
    private int _previous = -1;

    internal HorseFootstepSamples(Random random) => _random = random;

    internal int Next()
    {
        if (_next == _order.Length)
        {
            for (int i = _order.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            if (_order[0] == _previous)
            {
                int other = _random.Next(1, _order.Length);
                (_order[0], _order[other]) = (_order[other], _order[0]);
            }
            _next = 0;
        }
        int sample = _order[_next++];
        _previous = sample;
        return sample;
    }
}
