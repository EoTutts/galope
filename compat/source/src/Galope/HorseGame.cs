using System.Text.Json;
using System.Globalization;

namespace Galope;

internal readonly record struct SpeechLine(string PT, string EN, string ES);
internal readonly record struct InputEvent(string Action, string Physical = "", bool? Graphics = null);
internal enum ObstacleKind { Fence, TallFence, Logs, Pitbull }
internal readonly record struct ObstacleSpec(double Start, double Width, double Height,
    ObstacleKind Kind, int AudioId = 0, int SoundSample = 0)
{
    internal double End => Start + Width;
}
internal readonly record struct HorseView(string Screen, int Speed, double Distance,
    double JumpHeight, double JumpProgress, double WhipProgress, double DayPhase,
    int Cleared, int BestDistance, int Selection,
    double GaitPhase, int GaitSpeed, double ActualSpeed, IReadOnlyList<ObstacleSpec> Obstacles,
    FestivalPhase Festival, double FestivalTraversal, bool Bitten, DogView Dog);
internal readonly record struct DogView(DogPhase Phase, int AudioId, double ScreenX);

internal interface IHorseAudio : IDisposable
{
    void Hoof(HoofEvent hoof);
    void Saddle(SaddleCue cue);
    void SpeedUp();
    void AnimateWhip(double dt);
    void DustHoof(HoofFoot foot, double actualSpeed);
    void DustTakeoff(double actualSpeed);
    void AnimateDust(double dt, double actualSpeed);
    void Vocal(HorseVocalCue cue);
    void SetHorseHeight(double normalizedHeight);
    void Obstacle(int proximity, double screenX);
    void MoveObstacle(double screenX);
    void BeginObstacleSound(int id, int sample, double screenX);
    void MoveObstacleSound(int id, double screenX);
    void EndObstacleSound(int id);
    void StopObstacleSounds();
    void DogVocal(int sample, double screenX);
    void DogStep(int sample, double screenX, DogFoot foot);
    void MoveDog(double screenX);
    void DogBite(int sample, double screenX);
    void StopDog();
    void StartAmbient();
    void StopAmbient();
    void UpdateMotion(double speed, double dt, bool festival);
    void UpdateBackground(double dt);
    void StartFestival();
    void MoveFestival(double progress);
    void EndFestival();
    void Jump();
    void Crash();
    void StopAll();
}

internal sealed class HorseGame
{
    private enum Screen { Menu, Instructions, Racing, Paused, GameOver }
    internal const float PixelsPerUnit = 30;
    internal const float HorseFrontX = 523;
    internal const float HorseRearX = 437;
    private const double HorseRearOffset = (HorseFrontX - HorseRearX) / PixelsPerUnit;
    private const double OffscreenOffset = HorseFrontX / PixelsPerUnit;
    private const double DogWidth = 1.15;
    // More road speed at every pace. Stride distances sit between the former
    // and new speeds, so hoof cadence also grows without becoming frantic.
    private static readonly double[] WorldSpeed = [0, 11, 16, 22, 30, 42];
    private static readonly double[] GaitStrideSpeed = [0, 9.665, 14.035, 18.82, 24.86, 33.36];
    private const double ExtraSpeedPerObstacle = 0.95;
    private const double MaximumSpeed = 80;
    private const double Acceleration = 10.5;
    private const double GallopAcceleration = 14.0;
    private const double Deceleration = 19.0;
    private const double ReachedTolerance = 0.5;
    private const double WhipVisualDuration = 0.52;
    private readonly IHorseAudio? _audio;
    private readonly Action<HoofEvent> _playHoof;
    private readonly Action<HorseVocalCue> _playVocal;
    private readonly Action<DogFoot> _playDogFoot;
    private readonly Action<SpeechLine> _speak;
    private readonly Random _random;
    private readonly HorseGait _gait;
    private readonly HorseSaddle _saddle;
    private readonly HorseVocalDirector _vocals;
    private readonly HorseFestival _festival;
    private readonly HorseDogPursuit _dogPursuit;
    private readonly HorseDogGait _dogGait = new();
    private readonly Random _soundRandom;
    private readonly Func<double, int, ObstacleSpec>? _obstacleFactory;
    private readonly string _savePath;
    private Screen _screen = Screen.Menu;
    private int _menuIndex;
    private int _pauseIndex;
    private int _gameOverIndex;
    private int _speed = 2;
    private double _earnedPace = 2;
    private double _actualSpeed = WorldSpeed[2];
    private double _dustDriftSpeed;
    private double _warningClock = 1;
    private bool _obstacleSoundStarted;
    private int _nextObstacleAudioId;
    private int _cleared;
    private int _bestDistance;
    private double _distance;
    private double _dayAge;
    private readonly List<ObstacleSpec> _obstacles = [];
    private int _currentObstacleIndex;
    private double _jumpAge = -1;
    private double _jumpDuration = 1.7;
    private bool _speedCuePending;
    private double _whipVisualAge = WhipVisualDuration;
    private bool _systemSuspended;
    private bool _bitten;
    private double _dogBarkClock;
    private double _dogSpawnCooldown;
    private double _dogFootScreenX;
    private int _lastDogVocal = -1;
    private int _lastDogStep = -1;
    private int _lastDogBite = -1;
    internal bool Exiting { get; private set; }
    internal int Speed => _speed;
    internal double EarnedPace => _earnedPace;
    internal double EarnedSpeedTarget => Math.Min(MaximumSpeed,
        SpeedAtPace(_earnedPace) + Math.Max(0, _cleared - 15) * ExtraSpeedPerObstacle);
    internal double ActualSpeed => _actualSpeed;
    private double TargetPace => _festival.Phase != FestivalPhase.Waiting ? 2 :
        _dogPursuit.Phase == DogPhase.Chasing ? 5 : _earnedPace;
    private double DesiredSpeed => _festival.Phase != FestivalPhase.Waiting ? WorldSpeed[2] :
        _dogPursuit.Phase == DogPhase.Chasing ? Math.Max(WorldSpeed[5], EarnedSpeedTarget) :
        EarnedSpeedTarget;
    internal bool IsRacing => _screen == Screen.Racing;
    internal int Cleared => _cleared;
    internal double Distance => _distance;
    internal FestivalPhase Festival => _festival.Phase;
    internal DogPhase DogPhase => _dogPursuit.Phase;
    internal ObstacleSpec CurrentObstacle => _obstacles[_currentObstacleIndex];
    internal static int VisualFrame(int sample, double distance, double seconds, double seed = 0)
    {
        double motion = sample < 2 ? distance * .55 : seconds * (sample == 2 ? 5 : 4.5);
        return (int)Math.Floor(motion + seed * .17) & 3;
    }
    internal static int DogVisualFrame(double seconds, double seed, bool running) =>
        (int)Math.Floor(seconds * (running ? 8 : 3) + seed * .17) & 3;
    internal static double SpeedAtPace(double pace)
    {
        double bounded = Math.Clamp(pace, 1, 5);
        int lower = (int)Math.Floor(bounded);
        return lower == 5 ? WorldSpeed[5] :
            WorldSpeed[lower] + (WorldSpeed[lower + 1] - WorldSpeed[lower]) * (bounded - lower);
    }
    internal static double PaceAtSpeed(double speed)
    {
        for (int level = 1; level < 5; level++)
            if (speed < WorldSpeed[level + 1])
                return Math.Clamp(level + (speed - WorldSpeed[level]) /
                    (WorldSpeed[level + 1] - WorldSpeed[level]), 1, 5);
        return 5;
    }
    internal static string PaceLabel(double speed) => speed > WorldSpeed[5] + 0.5 ?
        $"5+ V{speed.ToString("F0", CultureInfo.InvariantCulture)}" :
        $"{PaceAtSpeed(speed).ToString("F1", CultureInfo.InvariantCulture)}/5";
    internal HorseView View => new(_screen.ToString(), _speed, _distance,
        JumpHeightAt(_jumpAge, _jumpDuration),
        _jumpAge < 0 ? -1 : Math.Clamp(_jumpAge / _jumpDuration, 0, 1),
        _screen == Screen.Racing && _whipVisualAge < WhipVisualDuration ?
            _whipVisualAge / WhipVisualDuration : -1,
        _dayAge / SkyCycle.SecondsPerCycle,
        _cleared, _bestDistance, _screen switch {
            Screen.Menu => _menuIndex, Screen.Paused => _pauseIndex, Screen.GameOver => _gameOverIndex, _ => 0
        }, _gait.Phase, _gait.ActiveSpeed, _actualSpeed, _obstacles, _festival.Phase,
        _festival.Traversal,
        _bitten, new DogView(_dogPursuit.Phase, _dogPursuit.AudioId, DogScreenX));

    internal HorseGame(IHorseAudio? audio, Action<SpeechLine> speak, Random? random = null,
        string? saveDir = null, Func<double, int, ObstacleSpec>? obstacleFactory = null,
        HorseFestival? festival = null)
    {
        _audio = audio;
        _playHoof = PlayHoof;
        _playVocal = cue => _audio?.Vocal(cue);
        _playDogFoot = PlayDogFoot;
        _speak = speak;
        _random = random ?? new Random();
        _gait = new HorseGait(new Random(_random.Next()), GaitStrideSpeed);
        _saddle = new HorseSaddle(new Random(_random.Next()));
        _vocals = new HorseVocalDirector(new Random(_random.Next()));
        _festival = festival ?? new HorseFestival(new Random(_random.Next()));
        _dogPursuit = new HorseDogPursuit(new Random(_random.Next()));
        _soundRandom = new Random(_random.Next());
        _obstacleFactory = obstacleFactory;
        _savePath = Path.Combine(saveDir ?? Environment.GetEnvironmentVariable("R36S_SAVE_DIR") ??
            "/home/player/.config/galope-infinito", "save.json");
        try
        {
            if (File.Exists(_savePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_savePath));
                if (doc.RootElement.TryGetProperty("bestDistance", out var best))
                    _bestDistance = Math.Max(0, best.GetInt32());
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Progresso ignorado: {ex.Message}"); }
    }

    private void Say(string pt, string en, string es) => _speak(new SpeechLine(pt, en, es));

    internal void Intro() => Say(
        "Galope. Menu. Jogar. Cima e baixo escolhem. Confirmar entra. Voltar sai.",
        "Galope. Menu. Play. Up and down choose. Confirm opens. Back exits.",
        "Galope. Menú. Jugar. Arriba y abajo eligen. Confirmar abre. Volver sale.");

    internal void Handle(InputEvent input)
    {
        if (Exiting || _systemSuspended) return;
        switch (_screen)
        {
            case Screen.Menu: HandleMenu(input.Action); break;
            case Screen.Instructions:
                if (input.Action is "confirm" or "start" or "jump") StartRace();
                else if (input.Action is "back" or "pause") { _screen = Screen.Menu; Intro(); }
                break;
            case Screen.Racing: HandleRace(input); break;
            case Screen.Paused: HandlePause(input.Action); break;
            case Screen.GameOver: HandleGameOver(input.Action); break;
        }
    }

    private void HandleMenu(string action)
    {
        if (action is "up" or "menu-up") { _menuIndex = (_menuIndex + 2) % 3; AnnounceMenu(); }
        else if (action is "down" or "menu-down") { _menuIndex = (_menuIndex + 1) % 3; AnnounceMenu(); }
        else if (action is "back" or "pause") Exiting = true;
        else if (action is "confirm" or "start" or "jump")
        {
            if (_menuIndex == 0) StartRace();
            else if (_menuIndex == 1)
            {
                _screen = Screen.Instructions;
                Say("O cavalo corre sem parar e acelera a cada obstáculo superado. B físico salta. Ouça os bips: quanto mais rápidos, mais perto está o obstáculo. Carros são largos, cães baixos e tambores altos. Fique no ar até passar por inteiro. Salte o pitbull: o cavalo acelera sozinho para fugir. No festival ele desacelera sozinho. Start abre a pausa. Confirmar começa.",
                    "The horse never stops and speeds up after each obstacle. The bottom face button jumps. Faster beeps mean the obstacle is closer. Cars are wide, dogs low, and drums tall. Stay airborne until you clear the whole obstacle. Jump over the pit bull: the horse accelerates to escape. It slows down at the festival. Start pauses. Confirm starts.",
                    "El caballo nunca se detiene y acelera tras cada obstáculo. El botón inferior salta. Los pitidos más rápidos indican que el obstáculo está cerca. Los coches son anchos, los perros bajos y los tambores altos. Permanezca en el aire hasta superarlos. Salte el pitbull: el caballo acelera para escapar. En el festival desacelera solo. Start pausa. Confirmar empieza.");
            }
            else Exiting = true;
        }
    }

    private void AnnounceMenu()
    {
        switch (_menuIndex)
        {
            case 0: Say("Jogar.", "Play.", "Jugar."); break;
            case 1: Say("Instruções.", "Instructions.", "Instrucciones."); break;
            case 2: Say("Sair para o hub.", "Exit to hub.", "Salir al centro de juegos."); break;
        }
    }

    private void StartRace()
    {
        _screen = Screen.Racing;
        _bitten = false;
        _dogPursuit.Reset();
        _dogGait.Reset();
        _festival.Reset();
        _dogBarkClock = 0;
        _dogSpawnCooldown = 0;
        _speed = 2;
        _earnedPace = 2;
        _actualSpeed = WorldSpeed[2];
        _dustDriftSpeed = 0;
        _distance = 0;
        _dayAge = 0;
        _cleared = 0;
        _obstacles.Clear();
        _nextObstacleAudioId = 0;
        _obstacles.Add(NextObstacle(0));
        _currentObstacleIndex = 0;
        _warningClock = 1;
        _obstacleSoundStarted = false;
        _jumpAge = -1;
        _jumpDuration = JumpDurationForSpeed(_speed);
        _gait.Reset(_speed);
        _saddle.Reset();
        _vocals.Reset();
        _speedCuePending = false;
        _whipVisualAge = WhipVisualDuration;
        _audio?.StopAll();
        _audio?.SetHorseHeight(0);
        _audio?.StartAmbient();
    }

    private ObstacleSpec NextObstacle(double after)
    {
        if (_obstacleFactory is not null)
            return _obstacleFactory(after, _cleared) with
            { AudioId = ++_nextObstacleAudioId, SoundSample = _soundRandom.Next(6) };
        double start;
        if (after == 0) start = _actualSpeed * (2.25 + _random.NextDouble() * 1.65);
        else
        {
            double target = Math.Max(_actualSpeed, DesiredSpeed);
            double gap = Math.Max(18, target * NextGapSeconds(_random));
            start = after + gap;
        }
        int soundSample = _soundRandom.Next(6);
        bool pitbull = _dogPursuit.Phase == DogPhase.None && _dogSpawnCooldown <= 0 &&
            _festival.CanScheduleDog && _random.NextDouble() < 0.10;
        var kind = pitbull ? ObstacleKind.Pitbull : soundSample switch
        {
            0 or 1 => ObstacleKind.Logs,       // carros: obstáculo largo e baixo
            2 => ObstacleKind.Fence,           // cães ao lado da estrada
            _ => ObstacleKind.TallFence        // tambores: obstáculo alto
        };
        if (kind == ObstacleKind.Pitbull)
            _dogSpawnCooldown = 32 + _random.NextDouble() * 16;
        ObstacleSpec obstacle = kind switch
        {
            ObstacleKind.Pitbull => new(start, DogWidth, 0.43, kind),
            ObstacleKind.TallFence => new(start, 1.05 + _random.NextDouble() * 0.65,
                0.54 + _random.NextDouble() * 0.12, kind),
            ObstacleKind.Logs => new(start, 1.70 + _random.NextDouble() * 1.40,
                0.32 + _random.NextDouble() * 0.13, kind),
            _ => new(start, 1.35 + _random.NextDouble() * 0.50,
                0.36 + _random.NextDouble() * 0.16, kind)
        };
        return obstacle with
        { AudioId = ++_nextObstacleAudioId, SoundSample = soundSample };
    }

    internal static double NextGapSeconds(Random random)
    {
        double band = random.NextDouble();
        return band < .55 ? 1.02 + random.NextDouble() * .38 :
            band < .80 ? 1.60 + random.NextDouble() * .85 :
            2.70 + random.NextDouble() * 1.50;
    }

    private void HandleRace(InputEvent input)
    {
        if (input.Physical == "bottom" || input.Action == "jump")
        {
            if (_jumpAge < 0)
            {
                _jumpAge = 0;
                _jumpDuration = JumpDurationForSpeed(ActualGaitLevel());
                _gait.Reset(ActualGaitLevel());
                if (_speed >= 2) _audio?.DustTakeoff(_actualSpeed);
                _audio?.Jump();
            }
        }
        else if (input.Action is "start" or "pause")
        {
            _whipVisualAge = WhipVisualDuration;
            _screen = Screen.Paused;
            _audio?.StopAmbient();
            _audio?.EndFestival();
            _audio?.StopObstacleSounds();
            _audio?.StopDog();
            _pauseIndex = 0;
            Say("Pausa. Continuar. Cima e baixo escolhem. Confirmar entra. Voltar continua.",
                "Paused. Continue. Up and down choose. Confirm opens. Back resumes.",
                "Pausa. Continuar. Arriba y abajo eligen. Confirmar abre. Volver continúa.");
        }
    }

    private void HandlePause(string action)
    {
        if (action is "up" or "menu-up") { _pauseIndex = (_pauseIndex + 2) % 3; AnnouncePause(); }
        else if (action is "down" or "menu-down") { _pauseIndex = (_pauseIndex + 1) % 3; AnnouncePause(); }
        else if (action is "back" or "pause" or "start") Resume();
        else if (action is "confirm" or "jump")
        {
            if (_pauseIndex == 0) Resume();
            else if (_pauseIndex == 1) StartRace();
            else Exiting = true;
        }
    }

    private void AnnouncePause()
    {
        switch (_pauseIndex)
        {
            case 0: Say("Continuar.", "Continue.", "Continuar."); break;
            case 1: Say("Recomeçar.", "Restart.", "Reiniciar."); break;
            case 2: Say("Sair para o hub.", "Exit to hub.", "Salir al centro de juegos."); break;
        }
    }

    private void Resume()
    {
        _screen = Screen.Racing;
        _audio?.StartAmbient();
        if (_festival.Phase is FestivalPhase.Warning or FestivalPhase.Active)
        {
            _audio?.StartFestival();
            _audio?.MoveFestival(_festival.Traversal);
        }
        RestoreObstacleSounds();
    }

    private void HandleGameOver(string action)
    {
        if (action is "up" or "down" or "menu-up" or "menu-down")
        {
            _gameOverIndex = 1 - _gameOverIndex;
            if (_gameOverIndex == 0) Say("Jogar novamente.", "Play again.", "Jugar de nuevo.");
            else Say("Sair para o hub.", "Exit to hub.", "Salir al centro de juegos.");
        }
        else if (action is "back" or "pause")
        {
            _audio?.StopAll();
            _screen = Screen.Menu;
            _menuIndex = 0;
            Intro();
        }
        else if (action is "confirm" or "jump" or "start")
        {
            if (_gameOverIndex == 0) StartRace();
            else Exiting = true;
        }
    }

    internal void Tick(double dt)
    {
        if (_systemSuspended || dt <= 0) return;
        dt = Math.Min(dt, 0.05); // no large collision skips after a slow frame
        _audio?.UpdateBackground(dt);
        if (_screen != Screen.Racing)
        {
            _audio?.UpdateMotion(0, dt, false);
            // Keep aging the last ground-level puffs after a collision. Their
            // drift loses momentum while the horse and the world stop.
            _audio?.AnimateDust(dt, _dustDriftSpeed);
            _dustDriftSpeed *= Math.Exp(-2.8 * dt);
            return;
        }
        _dayAge = (_dayAge + dt) % SkyCycle.SecondsPerCycle;
        _dogSpawnCooldown = Math.Max(0, _dogSpawnCooldown - dt);
        // The festival clock keeps running while a dog is present. Only its
        // warning waits, so repeated dogs cannot postpone it indefinitely.
        bool dogBlocksFestival = _dogPursuit.Phase != DogPhase.None ||
            CurrentObstacle.Kind == ObstacleKind.Pitbull;
        FestivalChange festivalChange = _festival.Advance(dt, !dogBlocksFestival);
        if (festivalChange == FestivalChange.Warning) _audio?.StartFestival();
        if (_festival.Phase is FestivalPhase.Warning or FestivalPhase.Active)
            _audio?.MoveFestival(_festival.Traversal);
        if (festivalChange == FestivalChange.Ended)
        {
            _audio?.MoveFestival(1);
            _audio?.EndFestival();
        }
        AdvanceSpeed(dt);
        _audio?.UpdateMotion(_actualSpeed, dt, _festival.Phase != FestivalPhase.Waiting);
        double jumpAgeAtStart = _jumpAge;
        if (_jumpAge >= 0) _jumpAge += dt;
        _whipVisualAge = Math.Min(WhipVisualDuration, _whipVisualAge + dt);
        _audio?.SetHorseHeight(JumpHeightAt(_jumpAge, _jumpDuration));
        _audio?.AnimateWhip(dt);
        _audio?.AnimateDust(dt, _actualSpeed);
        double previousDistance = _distance;
        _distance += _actualSpeed * dt;
        ObstacleSpec obstacle = CurrentObstacle;
        double remaining = obstacle.Start - _distance;
        double obstacleScreenX = ObstacleScreenX(obstacle);
        _audio?.MoveObstacle(obstacleScreenX);
        foreach (ObstacleSpec visible in _obstacles)
            _audio?.MoveObstacleSound(visible.AudioId, ObstacleScreenX(visible));
        if (!_obstacleSoundStarted && remaining <= _actualSpeed * 2.3)
        {
            _obstacleSoundStarted = true;
            if (obstacle.Kind == ObstacleKind.Pitbull)
            {
                _audio?.DogVocal(NextDogSample(ref _lastDogVocal, 10), obstacleScreenX);
                _dogBarkClock = 1.1 + _soundRandom.NextDouble() * 1.8;
            }
            else _audio?.BeginObstacleSound(obstacle.AudioId, obstacle.SoundSample, obstacleScreenX);
        }
        // Each pulse carries a continuous proximity value. Use time to contact
        // so the cadence and tone progress smoothly at every world speed.
        // Never catch up by playing a burst after a slow frame.
        if (remaining > 0 && remaining <= _actualSpeed * 2.3)
        {
            double lead = remaining / _actualSpeed;
            // Reach maximum only at the obstacle, not at the earlier jump cue.
            double proximity = Math.Clamp(1 - lead / 2.3, 0, 1);
            // Accumulate pulse phase at the current rate on every frame.
            // A fixed countdown would hold an old interval until the next
            // beep, which is audible as a sudden tempo change nearby.
            if (_warningClock >= 1)
            {
                _audio?.Obstacle((int)Math.Round(proximity * 1000), obstacleScreenX);
                _warningClock = 0;
            }
            else
            {
                double period = 0.075 * Math.Pow(4.2, 1 - proximity);
                _warningClock += dt / period;
                if (_warningClock >= 1)
                {
                    _audio?.Obstacle((int)Math.Round(proximity * 1000), obstacleScreenX);
                    _warningClock -= 1;
                }
            }
        }
        if (obstacle.Kind == ObstacleKind.Pitbull && _obstacleSoundStarted)
        {
            _audio?.MoveDog(obstacleScreenX);
            _dogBarkClock -= dt;
            if (_dogBarkClock <= 0)
            {
                _audio?.DogVocal(NextDogSample(ref _lastDogVocal, 10), obstacleScreenX);
                _dogBarkClock = 1.1 + _soundRandom.NextDouble() * 1.8;
            }
        }
        if (SweptCollision(obstacle, previousDistance, _distance, jumpAgeAtStart, dt))
        {
            if (obstacle.Kind == ObstacleKind.Pitbull) Bite(obstacleScreenX);
            else Crash();
            return;
        }
        if (_jumpAge >= _jumpDuration)
        {
            _jumpAge = -1;
            _gait.Reset(ActualGaitLevel());
        }
        if (_jumpAge < 0)
            _gait.Advance(_distance - previousDistance, ActualGaitLevel(), dt, _playHoof);
        _vocals.Advance(dt, _jumpAge < 0, _playVocal);
        bool dogJustStarted = false;
        double clearance = obstacle.Kind == ObstacleKind.Pitbull ? 0.12 : 0;
        if (_distance - HorseRearOffset > obstacle.End - clearance)
        {
            if (obstacle.Kind == ObstacleKind.Pitbull)
            {
                _dogPursuit.Start(obstacle.AudioId, obstacle.End);
                _dogGait.Reset();
                _dogBarkClock = 0.55;
                _speedCuePending = true;
                dogJustStarted = true;
            }
            _cleared++;
            _earnedPace = Math.Min(5, 2 + _cleared * 0.2);
            _currentObstacleIndex++;
            if (_currentObstacleIndex == _obstacles.Count)
                _obstacles.Add(NextObstacle(obstacle.End));
            _warningClock = 1;
            _obstacleSoundStarted = false;
        }
        while (_currentObstacleIndex > 0 && _obstacles[0].End < _distance - OffscreenOffset)
        {
            _audio?.EndObstacleSound(_obstacles[0].AudioId);
            _obstacles.RemoveAt(0);
            _currentObstacleIndex--;
        }
        if (!dogJustStarted) AdvanceDog(dt);
    }

    private double DogScreenX => HorseFrontX +
        (_dogPursuit.Front - _distance - DogWidth * 0.5) * PixelsPerUnit;

    private int NextDogSample(ref int previous, int count)
    {
        int sample = _soundRandom.Next(previous < 0 ? count : count - 1);
        if (previous >= 0 && sample >= previous) sample++;
        previous = sample;
        return sample;
    }

    private void AdvanceDog(double dt)
    {
        if (_dogPursuit.Phase == DogPhase.None) return;
        DogChange change = _dogPursuit.Advance(dt, _distance - HorseRearOffset,
            _actualSpeed >= WorldSpeed[5] - ReachedTolerance);
        if (change == DogChange.Caught)
        {
            Bite(DogScreenX);
            return;
        }
        if (change == DogChange.Gone)
        {
            _audio?.StopDog();
            return;
        }
        double screenX = DogScreenX;
        _audio?.MoveDog(screenX);
        if (_dogPursuit.Phase != DogPhase.Chasing) return;
        _dogFootScreenX = screenX;
        _dogGait.Advance(dt, _dogPursuit.Speed, _playDogFoot);
        _dogBarkClock -= dt;
        if (_dogBarkClock <= 0)
        {
            _audio?.DogVocal(NextDogSample(ref _lastDogVocal, 10), screenX);
            _dogBarkClock = 0.75 + _soundRandom.NextDouble() * 1.3;
        }
    }

    private void PlayDogFoot(DogFoot foot) =>
        _audio?.DogStep(NextDogSample(ref _lastDogStep, 4), _dogFootScreenX, foot);

    private void AdvanceSpeed(double dt)
    {
        double target = DesiredSpeed;
        double levelMix = (TargetPace - 1) / 4.0;
        double rate = target < _actualSpeed ? Deceleration :
            Acceleration + (GallopAcceleration - Acceleration) * levelMix;
        _actualSpeed += Math.Clamp(target - _actualSpeed, -rate * dt, rate * dt);
        _speed = ActualGaitLevel();
    }

    private int ActualGaitLevel()
    {
        if (_actualSpeed < (WorldSpeed[1] + WorldSpeed[2]) / 2) return 1;
        if (_actualSpeed < (WorldSpeed[2] + WorldSpeed[3]) / 2) return 2;
        if (_actualSpeed < (WorldSpeed[3] + WorldSpeed[4]) / 2) return 3;
        return _actualSpeed < (WorldSpeed[4] + WorldSpeed[5]) / 2 ? 4 : 5;
    }

    private double ObstacleScreenX(ObstacleSpec obstacle) =>
        HorseFrontX + (obstacle.Start + obstacle.Width * 0.5 - _distance) * PixelsPerUnit;

    private void RestoreObstacleSounds()
    {
        for (int i = 0; i < _currentObstacleIndex; i++)
        {
            ObstacleSpec passed = _obstacles[i];
            if (passed.Kind == ObstacleKind.Pitbull) continue;
            _audio?.BeginObstacleSound(passed.AudioId, passed.SoundSample,
                ObstacleScreenX(passed));
        }
        if (_obstacleSoundStarted && CurrentObstacle.Start > _distance)
        {
            ObstacleSpec current = CurrentObstacle;
            if (current.Kind != ObstacleKind.Pitbull)
                _audio?.BeginObstacleSound(current.AudioId, current.SoundSample,
                    ObstacleScreenX(current));
        }
    }

    private void PlayHoof(HoofEvent hoof)
    {
        _audio?.Hoof(hoof);
        if (_speed >= 2 && (hoof.Foot is HoofFoot.HindLeft or HoofFoot.HindRight))
            _audio?.DustHoof(hoof.Foot, _actualSpeed);
        if (_speedCuePending && hoof.Primary)
        {
            _speedCuePending = false;
            _whipVisualAge = 0;
            _audio?.SpeedUp();
        }
        if (!hoof.StartsCycle) return;
        SaddleCycle cycle = _saddle.NextCycle(hoof.Speed);
        _audio?.Saddle(cycle.Soft);
        if (cycle.Strong is SaddleCue strong) _audio?.Saddle(strong);
    }

    private bool SweptCollision(ObstacleSpec obstacle, double previousDistance,
        double nextDistance, double jumpAgeAtStart, double dt)
    {
        // The horse occupies a horizontal interval. Keep testing until its hind legs
        // have passed the obstacle, including a landing while the sprites overlap.
        if (nextDistance <= obstacle.Start || previousDistance - HorseRearOffset >= obstacle.End)
            return false;
        double travelled = nextDistance - previousDistance;
        double enter = Math.Clamp((obstacle.Start - previousDistance) / travelled, 0, 1);
        double leave = Math.Clamp((obstacle.End + HorseRearOffset - previousDistance) / travelled, 0, 1);
        if (enter >= leave) return false;
        double firstHeight = JumpHeightAt(jumpAgeAtStart < 0 ? -1 : jumpAgeAtStart + enter * dt,
            _jumpDuration);
        double lastHeight = JumpHeightAt(jumpAgeAtStart < 0 ? -1 : jumpAgeAtStart + leave * dt,
            _jumpDuration);
        return Math.Min(firstHeight, lastHeight) <= obstacle.Height;
    }

    private static double JumpDurationForSpeed(int speed) => 1.15 - 0.10 * (speed - 1);

    private double JumpLeadSeconds(ObstacleSpec obstacle) => Math.Clamp(
        (JumpDurationForSpeed(ActualGaitLevel()) -
            (obstacle.Width + HorseRearOffset) / _actualSpeed) / 2,
        0.25, 0.6);

    private double CueLeadSeconds(ObstacleSpec obstacle, int stage)
    {
        double jumpLead = JumpLeadSeconds(obstacle);
        // At high speed the obstacle covers more ground between frames. Give
        // the cues enough separation to be heard, then shorten the intervals
        // as the obstacle approaches. Every cue uses its current screen X.
        double visibleLead = ((960 - HorseFrontX) / PixelsPerUnit -
            obstacle.Width * 0.5) / _actualSpeed;
        double firstLead = Math.Max(jumpLead + 0.60, visibleLead);
        double fraction = stage switch { 1 => 1, 2 => 0.58, 3 => 0.25, _ => 0 };
        return jumpLead + (firstLead - jumpLead) * fraction;
    }

    private static double JumpHeightAt(double age, double duration) => age < 0 || age >= duration ? 0 :
        Math.Max(0, Math.Sin(Math.PI * age / duration));

    internal void SuspendForSystemMenu()
    {
        _systemSuspended = true;
        _whipVisualAge = WhipVisualDuration;
        _audio?.StopAll();
    }

    internal void ResumeFromSystemMenu()
    {
        if (!_systemSuspended) return;
        _systemSuspended = false;
        if (_screen != Screen.Racing) return;
        _audio?.StartAmbient();
        if (_festival.Phase is FestivalPhase.Warning or FestivalPhase.Active)
        {
            _audio?.StartFestival();
            _audio?.MoveFestival(_festival.Traversal);
        }
        RestoreObstacleSounds();
        _warningClock = 1;
    }

    private void Crash()
    {
        _screen = Screen.GameOver;
        _bitten = false;
        _dustDriftSpeed = _actualSpeed;
        _gameOverIndex = 0;
        _audio?.Crash();
        int reached = (int)_distance;
        if (reached > _bestDistance) { _bestDistance = reached; Save(); }
        Say($"Colisão. {reached} metros, {_cleared} obstáculos. Recorde: {_bestDistance} metros.",
            $"Collision. {reached} meters, {_cleared} obstacles. Best: {_bestDistance} meters.",
            $"Colisión. {reached} metros, {_cleared} obstáculos. Récord: {_bestDistance} metros.");
    }

    private void Bite(double screenX)
    {
        _screen = Screen.GameOver;
        _bitten = true;
        _dustDriftSpeed = _actualSpeed;
        _gameOverIndex = 0;
        _audio?.StopDog();
        _audio?.DogBite(NextDogSample(ref _lastDogBite, 10), screenX);
        _audio?.Crash();
        int reached = (int)_distance;
        if (reached > _bestDistance) { _bestDistance = reached; Save(); }
        Say($"Mordida. {reached} metros, {_cleared} obstáculos. Recorde: {_bestDistance} metros.",
            $"Dog bite. {reached} meters, {_cleared} obstacles. Best: {_bestDistance} meters.",
            $"Mordedura. {reached} metros, {_cleared} obstáculos. Récord: {_bestDistance} metros.");
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            string temp = _savePath + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new { bestDistance = _bestDistance });
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _savePath, overwrite: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Progresso não salvo: {ex.Message}"); }
    }
}
