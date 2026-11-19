namespace Galope;

internal readonly record struct SaddleCue(int Speed, bool Strong, double GainDb);
internal readonly record struct SaddleCycle(SaddleCue Soft, SaddleCue? Strong);

// One soft leather layer per gait cycle. Accents rise through the first three
// speeds; canter and gallop add a stronger creak, as in Riveria.
internal sealed class HorseSaddle
{
    private static readonly double[] AccentChance = [0, 0.30, 0.65, 0.86, 0, 0];
    private readonly Random _random;
    private double _fastMix;

    internal HorseSaddle(Random random) => _random = random;

    internal void Reset() => _fastMix = 0;

    internal SaddleCycle NextCycle(int speed)
    {
        double target = speed >= 4 ? 1 : 0;
        _fastMix = Math.Clamp(_fastMix + Math.Clamp(target - _fastMix, -0.5, 0.5), 0, 1);
        double speedMix = speed / 5.0;
        bool accented = speed <= 3 && _fastMix == 0 &&
            _random.NextDouble() < AccentChance[speed];
        double lowBed = -17.5 + 1.5 * speedMix;
        double fastBed = -20.5 + 1.5 * speedMix;
        double softGain = accented ? -11.5 + 2.8 * speedMix :
            lowBed + (fastBed - lowBed) * _fastMix;
        SaddleCue soft = new(speed, false, softGain);
        if (_fastMix <= 0) return new(soft, null);
        double targetChance = speed == 4 ? 0.84 : 1.0;
        double strongChance = 0.55 + (targetChance - 0.55) * _fastMix;
        if (_random.NextDouble() > strongChance) return new(soft, null);
        double strongGain = -8 + 4 * speedMix + 20 * Math.Log10(_fastMix);
        return new(soft, new SaddleCue(speed, true, strongGain));
    }
}
