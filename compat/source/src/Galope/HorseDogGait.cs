namespace Galope;

internal enum DogFoot { FrontLeft, FrontRight, HindRight, HindLeft }

// At lower speed the diagonal pairs form a trot. Fast dogs commonly use a
// rotary gallop: forelimb pair, a compressed flight, hindlimb pair, then an
// extended flight before the next stride. The timing is deliberately grouped,
// so four paw impacts never sound like a uniform metronome.
internal sealed class HorseDogGait
{
    private static readonly (DogFoot Foot, double Phase)[] Trot = [
        (DogFoot.FrontLeft, 0), (DogFoot.HindRight, 0.055),
        (DogFoot.FrontRight, 0.50), (DogFoot.HindLeft, 0.555)
    ];
    private static readonly (DogFoot Foot, double Phase)[] RotaryGallop = [
        (DogFoot.FrontLeft, 0), (DogFoot.FrontRight, 0.13),
        (DogFoot.HindRight, 0.51), (DogFoot.HindLeft, 0.64)
    ];
    private double _phase = -0.000001;
    private bool _galloping;

    internal double Phase => Math.Max(0, _phase);

    internal void Reset()
    {
        _phase = -0.000001;
        _galloping = false;
    }

    internal void Advance(double dt, double speed, Action<DogFoot> footfall)
    {
        if (dt <= 0) return;
        bool gallop = speed >= 8;
        if (gallop != _galloping)
        {
            _galloping = gallop;
            _phase = -0.000001;
        }
        double strideSeconds = gallop ?
            0.56 - 0.20 * Math.Clamp((speed - 8) / 16, 0, 1) :
            0.84 - 0.22 * Math.Clamp(speed / 8, 0, 1);
        double end = _phase + dt / strideSeconds;
        var pattern = gallop ? RotaryGallop : Trot;
        for (int cycle = (int)Math.Floor(_phase); cycle <= (int)Math.Floor(end); cycle++)
            foreach (var beat in pattern)
            {
                double time = cycle + beat.Phase;
                if (time > _phase && time <= end) footfall(beat.Foot);
            }
        _phase = end - Math.Floor(end);
    }
}
