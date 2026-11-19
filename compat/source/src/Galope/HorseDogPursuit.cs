namespace Galope;

internal enum DogPhase { None, Chasing, FallingBehind }
internal enum DogChange { None, Caught, Escaped, Gone }

// World coordinates keep the dog at its obstacle until the horse's hind legs
// have nearly cleared it. The dog then accelerates from rest. The only way it
// gives up is a sustained run at the fifth speed; slower horses are eventually
// caught, even if the pursuit lasts beyond its randomized minimum duration.
internal sealed class HorseDogPursuit
{
    private readonly Random _random;
    private double _targetSpeed;
    private double _acceleration;
    private double _retreatAge;
    private double _fifthSpeedAge;

    internal DogPhase Phase { get; private set; }
    internal int AudioId { get; private set; }
    internal double Front { get; private set; }
    internal double Speed { get; private set; }
    internal double Age { get; private set; }
    internal double MinimumDuration { get; private set; }

    internal HorseDogPursuit(Random random) => _random = random;

    internal void Reset()
    {
        Phase = DogPhase.None;
        AudioId = 0;
        Front = Speed = Age = MinimumDuration = _retreatAge = _fifthSpeedAge = 0;
    }

    internal void Start(int audioId, double front)
    {
        Phase = DogPhase.Chasing;
        AudioId = audioId;
        Front = front;
        Speed = Age = _retreatAge = _fifthSpeedAge = 0;
        MinimumDuration = 9 + _random.NextDouble() * 5;
        _targetSpeed = 34 + _random.NextDouble() * 3;
        _acceleration = 6.0 + _random.NextDouble() * 2;
    }

    internal DogChange Advance(double dt, double horseRear, bool horseAtFifthSpeed)
    {
        if (Phase == DogPhase.None || dt <= 0) return DogChange.None;
        if (Phase == DogPhase.FallingBehind)
        {
            _retreatAge += dt;
            Speed = Math.Max(0, Speed - 4 * dt);
            Front += Speed * dt;
            if (_retreatAge < 1.6) return DogChange.None;
            Reset();
            return DogChange.Gone;
        }

        Age += dt;
        _fifthSpeedAge = horseAtFifthSpeed ? _fifthSpeedAge + dt : 0;
        Speed = Math.Min(_targetSpeed, Speed + _acceleration * dt);
        Front += Speed * dt;
        double gap = horseRear - Front;
        if (Age >= 0.5 && gap <= 0) return DogChange.Caught;
        if (Age >= MinimumDuration && _fifthSpeedAge >= 1.2 && gap >= 4)
        {
            Phase = DogPhase.FallingBehind;
            return DogChange.Escaped;
        }
        return DogChange.None;
    }
}
