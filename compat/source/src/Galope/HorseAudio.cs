using System.Runtime.InteropServices;
using NVorbis;

namespace Galope;

// Positional OGGs decode to mono for OpenAL's 3D panning. The two road
// ambience OGGs remain stereo and play as non-spatial background beds.
internal sealed class HorseAudio : IHorseAudio
{
    private const string Lib = "libopenal.so.1";
    private const int Mono16 = 0x1101;
    private const int Stereo16 = 0x1103;
    private const int BufferChannels = 0x2003;
    private const int Position = 0x1004;
    private const int Velocity = 0x1006;
    private const int Gain = 0x100A;
    private const int Pitch = 0x1003;
    private const int SecOffset = 0x1024;
    private const int Buffer = 0x1009;
    private const int SourceRelative = 0x202;
    private const int Looping = 0x1007;
    private const int RolloffFactor = 0x1021;
    private const int Orientation = 0x100F;
    private const float WhipDuration = 0.52f;
    private const float DustDuration = 1.30f;
    private const float GravelSampleDuration = 15f;
    private static readonly string[] VocalSamples = [
        "horse_exertion_01", "horse_exertion_02", "snort_01", "snort_02",
        "snort_03", "snort_04", "snort_05"
    ];
    private static readonly string[] PainSamples = [
        "pain_08", "pain_09", "pain_10", "pain_16", "pain_18", "pain_19"
    ];
    private static readonly string[] AmbientSamples = ["loop_country_road", "loop_country_road2"];
    private static readonly string[] ObstacleSamples = [
        "loop_car_alarm", "loop_car11", "loop_dogs3",
        "loop_drums9", "loop_drums10", "loop_drums2"
    ];
    private static readonly float[] ObstacleGains = [0.35f, 0.28f, 0.35f, 0.30f, 0.28f, 0.27f];
    private static readonly float[] PitchBySpeed = [0, 0.93f, 0.98f, 1.04f, 1.12f, 1.20f];
    private readonly Dictionary<string, uint> _buffers = new();
    private long _bufferBytes;
    internal int LoadedBufferCount => _buffers.Count;
    internal long LoadedPcmBytes => _bufferBytes;
    private readonly Random _sampleRandom = new();
    private readonly HorseFootstepSamples _footsteps;
    private int _lastSoftSaddle = -1;
    private int _lastStrongSaddle = -1;
    private int _saddleSourceIndex;
    private int _dustSourceIndex;
    private int _lastPain = -1;
    private float _horseLift;
    private float _whipAge = WhipDuration;
    private SoundPoint _actionPoint = HorseSpatial.Body;
    private SoundPoint _obstaclePoint = HorseSpatial.ObstacleAt(960);
    private IntPtr _device;
    private IntPtr _context;
    private readonly uint[] _hooves = new uint[4];
    private readonly uint[] _saddles = new uint[2];
    private readonly uint[] _dust = new uint[8];
    private readonly uint[] _ambient = new uint[2];
    private readonly uint[] _obstacleBeds = new uint[2];
    private readonly uint[] _dogVoices = new uint[2];
    private readonly uint[] _dogSteps = new uint[2];
    private readonly DogFoot[] _dogStepFeet = new DogFoot[2];
    private readonly int[] _obstacleBedIds = new int[2];
    private readonly float[] _obstacleBedGain = new float[2];
    private readonly float[] _obstacleBedTailAge = new float[2];
    private readonly bool[] _obstacleBedTailing = new bool[2];
    private readonly DustPuff[] _dustPuffs = [new(), new(), new(), new(), new(), new(), new(), new()];
    private uint _cue;
    private uint _action;
    private uint _whip;
    private uint _vocal;
    private uint _festivalMusic;
    private uint _dogBite;
    private uint _wind;
    private float _windGain;
    private float _windWanted;
    private double _motionSpeed;
    private double _motionDt = 1.0 / 60;
    private SoundPoint _previousDogPoint;
    private SoundPoint _previousFestivalPoint;
    private bool _dogPointValid;
    private bool _festivalPointValid;
    private float _dogVelocityX;
    private int _nextDogVoice;
    private int _nextDogStep;
    private int _nextObstacleBed;
    private bool _ambientRunning;
    private int _ambientSlot;
    private int _ambientIncoming = -1;
    private float _ambientAge;
    private float _ambientCrossfadeAge;
    private float _ambientSwitchAfter;
    private bool _festivalPlaying;
    private bool _festivalWanted;
    private float _festivalGain;
    private float _festivalProgress;

    private sealed class DustPuff
    {
        internal bool Active;
        internal HoofFoot Foot;
        internal float Age;
        internal double Travelled;
        internal float Gain;
    }

    static HorseAudio()
    {
        NativeLibrary.SetDllImportResolver(typeof(HorseAudio).Assembly, (name, _, _) =>
        {
            if (name == Lib && OperatingSystem.IsWindows())
            {
                if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "OpenAL32.dll"), out var handle))
                    return handle;
                if (NativeLibrary.TryLoad("OpenAL32.dll", out handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport(Lib)] private static extern IntPtr alcOpenDevice(IntPtr name);
    [DllImport(Lib)] private static extern IntPtr alcCreateContext(IntPtr device, IntPtr attributes);
    [DllImport(Lib)] private static extern byte alcMakeContextCurrent(IntPtr context);
    [DllImport(Lib)] private static extern void alcDestroyContext(IntPtr context);
    [DllImport(Lib)] private static extern byte alcCloseDevice(IntPtr device);
    [DllImport(Lib)] private static extern void alGenSources(int count, out uint source);
    [DllImport(Lib)] private static extern void alDeleteSources(int count, ref uint source);
    [DllImport(Lib)] private static extern void alGenBuffers(int count, out uint buffer);
    [DllImport(Lib)] private static extern void alDeleteBuffers(int count, ref uint buffer);
    [DllImport(Lib)] private static extern void alSourceStop(uint source);
    [DllImport(Lib)] private static extern void alSourcei(uint source, int param, int value);
    [DllImport(Lib)] private static extern void alSourcef(uint source, int param, float value);
    [DllImport(Lib)] private static extern void alSource3f(uint source, int param, float x, float y, float z);
    [DllImport(Lib)] private static extern void alListener3f(int param, float x, float y, float z);
    [DllImport(Lib)] private static extern void alListenerfv(int param, float[] values);
    [DllImport(Lib)] private static extern void alSourcePlay(uint source);
    [DllImport(Lib)] private static extern void alBufferData(uint buffer, int format, byte[] data, int size, int frequency);
    [DllImport(Lib)] private static extern void alGetBufferi(uint buffer, int param, out int value);
    [DllImport(Lib)] private static extern int alGetError();
    [DllImport(Lib)] private static extern void alSpeedOfSound(float value);
    [DllImport(Lib)] private static extern void alDopplerFactor(float value);

    internal HorseAudio()
    {
        _footsteps = new HorseFootstepSamples(_sampleRandom);
        _device = alcOpenDevice(IntPtr.Zero);
        if (_device == IntPtr.Zero) throw new InvalidOperationException("OpenAL não abriu o dispositivo padrão");
        _context = alcCreateContext(_device, IntPtr.Zero);
        if (_context == IntPtr.Zero || alcMakeContextCurrent(_context) == 0)
            throw new InvalidOperationException("OpenAL não criou o contexto");
        for (int i = 0; i < _hooves.Length; i++) alGenSources(1, out _hooves[i]);
        for (int i = 0; i < _saddles.Length; i++) alGenSources(1, out _saddles[i]);
        for (int i = 0; i < _dust.Length; i++) alGenSources(1, out _dust[i]);
        for (int i = 0; i < _ambient.Length; i++) alGenSources(1, out _ambient[i]);
        for (int i = 0; i < _obstacleBeds.Length; i++) alGenSources(1, out _obstacleBeds[i]);
        for (int i = 0; i < _dogVoices.Length; i++) alGenSources(1, out _dogVoices[i]);
        for (int i = 0; i < _dogSteps.Length; i++) alGenSources(1, out _dogSteps[i]);
        alGenSources(1, out _cue);
        alGenSources(1, out _action);
        alGenSources(1, out _whip);
        alGenSources(1, out _vocal);
        alGenSources(1, out _festivalMusic);
        alGenSources(1, out _dogBite);
        alGenSources(1, out _wind);
        alListener3f(Position, 0, 0, 0);
        alListenerfv(Orientation, [0, 0, -1, 0, 1, 0]);
        alSpeedOfSound(30f);
        alDopplerFactor(1f);
        foreach (uint source in _hooves.Concat(_saddles).Concat(_dust)
            .Concat(_ambient).Concat(_obstacleBeds).Concat(_dogVoices).Concat(_dogSteps)
            .Concat([_cue, _action, _whip, _vocal, _festivalMusic, _dogBite, _wind]))
        {
            alSourcei(source, SourceRelative, 0);
            alSourcef(source, RolloffFactor, 0.38f);
        }
        foreach (uint source in _ambient.Append(_wind))
        {
            alSourcei(source, SourceRelative, 1);
            alSourcef(source, RolloffFactor, 0);
        }
        foreach (uint source in _obstacleBeds.Concat(_dogVoices).Concat(_dogSteps)
            .Concat([_cue, _festivalMusic]))
            alSourcef(source, RolloffFactor, 0.22f);
        Check();
    }

    private static void Check()
    {
        int error = alGetError();
        if (error != 0) throw new InvalidOperationException($"OpenAL: erro {error}");
    }

    private uint GetOggBuffer(string name)
    {
        if (_buffers.TryGetValue(name, out uint existing)) return existing;
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "audio", name + ".ogg");
        using var vorbis = new VorbisReader(path);
        int channels = vorbis.Channels;
        if (channels != (AmbientSamples.Contains(name) || name == "wind_run" ? 2 : 1))
            throw new InvalidDataException($"Número de canais inesperado: {path}");
        int capacity = (int)Math.Clamp(
            vorbis.TotalTime.TotalSeconds * vorbis.SampleRate * channels * 2,
            0, 32 * 1024 * 1024);
        using var output = new MemoryStream(capacity);
        float[] samples = new float[4096 * channels];
        byte[] block = new byte[samples.Length * 2];
        int count;
        while ((count = vorbis.ReadSamples(samples, 0, samples.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                short sample = (short)(Math.Clamp(samples[i], -1, 1) * short.MaxValue);
                block[i * 2] = (byte)sample;
                block[i * 2 + 1] = (byte)(sample >> 8);
            }
            output.Write(block, 0, count * 2);
        }
        int pcmLength = checked((int)output.Length);
        if (pcmLength == 0) throw new InvalidDataException(path);
        alGenBuffers(1, out uint buffer);
        alBufferData(buffer, channels == 2 ? Stereo16 : Mono16,
            output.GetBuffer(), pcmLength, vorbis.SampleRate);
        Check();
        _bufferBytes += pcmLength;
        _buffers.Add(name, buffer);
        return buffer;
    }

    internal int ChannelsOf(string name)
    {
        uint buffer = GetOggBuffer(name);
        alGetBufferi(buffer, BufferChannels, out int channels);
        Check();
        return channels;
    }

    private void Play(uint source, uint buffer, SoundPoint point, float gain,
        float pitch = 1, bool followsHorse = true, float offsetSeconds = 0)
    {
        alSourceStop(source);
        alSourcei(source, Buffer, 0);
        alSourcei(source, Buffer, (int)buffer);
        alSourcei(source, Looping, 0);
        alSource3f(source, Position, point.X,
            point.Y + (followsHorse ? _horseLift : 0), point.Z);
        alSource3f(source, Velocity, 0, 0, 0);
        alSourcef(source, Gain, gain);
        alSourcef(source, Pitch, pitch);
        if (offsetSeconds > 0) alSourcef(source, SecOffset, offsetSeconds);
        alSourcePlay(source);
        Check();
    }

    public void Hoof(HoofEvent hoof)
    {
        bool front = hoof.Foot is HoofFoot.FrontLeft or HoofFoot.FrontRight;
        bool left = hoof.Foot is HoofFoot.HindLeft or HoofFoot.FrontLeft;
        int sample = _footsteps.Next();
        uint buffer = GetOggBuffer($"hoof_fs_{sample + 1:D2}");
        float speedBoost = (float)Math.Clamp((_motionSpeed - 42) / 38, 0, 1);
        float pitch = PitchBySpeed[hoof.Speed] + speedBoost * .13f +
            (front ? 0.012f : -0.015f) +
            (left ? -0.004f : 0.004f);
        float gain = (float)(0.56 * Math.Pow(10, hoof.AccentDb / 20));
        Play(_hooves[(int)hoof.Foot], buffer, HorseSpatial.Hoof(hoof.Foot), gain, pitch,
            followsHorse: false);
    }

    public void DustHoof(HoofFoot foot, double actualSpeed) => EmitDust(foot, actualSpeed, 1);

    public void DustTakeoff(double actualSpeed)
    {
        // The last push leaves a slightly wider puff even if the next hind
        // hoof contact would otherwise have happened after takeoff.
        EmitDust(HoofFoot.HindLeft, actualSpeed, 0.75f);
        EmitDust(HoofFoot.HindRight, actualSpeed, 0.75f);
    }

    private void EmitDust(HoofFoot foot, double actualSpeed, float strength)
    {
        uint buffer = GetOggBuffer("slide_gravel");
        int index = _dustSourceIndex++ % _dust.Length;
        DustPuff puff = _dustPuffs[index];
        puff.Active = true;
        puff.Foot = foot;
        puff.Age = 0;
        puff.Travelled = 0;
        puff.Gain = HorseSpatial.DustGain(actualSpeed) * strength *
            (0.88f + 0.22f * (float)_sampleRandom.NextDouble());
        float pitch = 0.90f + 0.19f * (float)_sampleRandom.NextDouble();
        float sampleStart = (float)(_sampleRandom.NextDouble() *
            (GravelSampleDuration - DustDuration * pitch));
        Play(_dust[index], buffer, HorseSpatial.DustAt(foot, 0, 0), 0, pitch,
            followsHorse: false, offsetSeconds: sampleStart);
    }

    public void AnimateDust(double dt, double actualSpeed)
    {
        for (int i = 0; i < _dustPuffs.Length; i++)
        {
            DustPuff puff = _dustPuffs[i];
            if (!puff.Active) continue;
            puff.Age += (float)dt;
            if (puff.Age >= DustDuration)
            {
                puff.Active = false;
                alSourceStop(_dust[i]);
                continue;
            }
            puff.Travelled += actualSpeed * dt;
            float progress = puff.Age / DustDuration;
            SoundPoint point = HorseSpatial.DustAt(puff.Foot, puff.Travelled, progress);
            alSource3f(_dust[i], Position, point.X, point.Y, point.Z);
            alSourcef(_dust[i], Gain, puff.Gain * HorseSpatial.DustFade(progress));
        }
    }

    public void Vocal(HorseVocalCue cue)
    {
        string key = VocalSamples[(int)cue];
        float gain = cue is HorseVocalCue.Exertion1 or HorseVocalCue.Exertion2 ? 0.62f : 0.54f;
        Play(_vocal, GetOggBuffer(key), HorseSpatial.Head, gain);
    }

    private int NextNonRepeating(int previous)
    {
        int index = _sampleRandom.Next(previous < 0 ? 10 : 9);
        if (previous >= 0 && index >= previous) index++;
        return index;
    }

    public void Saddle(SaddleCue cue)
    {
        int index;
        string key;
        if (cue.Strong)
        {
            index = NextNonRepeating(_lastStrongSaddle);
            _lastStrongSaddle = index;
            key = $"saddle_creak_{index + 1:D2}";
        }
        else
        {
            index = NextNonRepeating(_lastSoftSaddle);
            _lastSoftSaddle = index;
            key = $"saddle_soft_{index + 1:D2}";
        }
        uint buffer = GetOggBuffer(key);
        uint source = _saddles[_saddleSourceIndex++ % _saddles.Length];
        float gain = (float)Math.Pow(10, cue.GainDb / 20);
        float pitch = 0.96f + (float)_sampleRandom.NextDouble() * 0.085f +
            (float)Math.Clamp((_motionSpeed - 42) / 38, 0, 1) * .10f;
        Play(source, buffer, HorseSpatial.Rider, gain, pitch);
    }

    public void SpeedUp()
    {
        uint buffer = GetOggBuffer("speed_cue_whip_01");
        float pitch = 0.98f + (float)_sampleRandom.NextDouble() * 0.045f;
        _whipAge = 0;
        Play(_whip, buffer, HorseSpatial.WhipAt(0), 0.84f, pitch);
    }

    public void AnimateWhip(double dt)
    {
        if (_whipAge >= WhipDuration) return;
        _whipAge = Math.Min(WhipDuration, _whipAge + (float)dt);
        MoveWithHorse(_whip, HorseSpatial.WhipAt(_whipAge / WhipDuration));
    }

    public void SetHorseHeight(double normalizedHeight)
    {
        float lift = (float)Math.Clamp(normalizedHeight * 0.7, 0, 0.7);
        if (Math.Abs(lift - _horseLift) < 0.01f) return;
        _horseLift = lift;
        MoveWithHorse(_action, _actionPoint);
        MoveWithHorse(_whip, HorseSpatial.WhipAt(_whipAge / WhipDuration));
        foreach (uint source in _saddles) MoveWithHorse(source, HorseSpatial.Rider);
        MoveWithHorse(_vocal, HorseSpatial.Head);
    }

    private void MoveWithHorse(uint source, SoundPoint point) =>
        alSource3f(source, Position, point.X, point.Y + _horseLift, point.Z);

    private float ObstacleVelocityX => (float)Math.Max(-3,
        -_motionSpeed * HorseGame.PixelsPerUnit / 440);

    public void UpdateMotion(double speed, double dt, bool festival)
    {
        _motionSpeed = Math.Max(0, speed);
        _motionDt = Math.Max(dt, 1.0 / 240);
        double pace = HorseGame.PaceAtSpeed(_motionSpeed);
        double extra = Math.Clamp((_motionSpeed - 42) / 38, 0, 1);
        _windWanted = speed <= 0 || !_ambientRunning ? 0 :
            (float)((0.025 + Math.Clamp((pace - 2) / 3, 0, 1) * 0.075 + extra * .055) *
                (festival ? 0.55 : 1));
        alSourcef(_wind, Pitch, (float)(0.85 + Math.Clamp((pace - 2) / 3, 0, 1) * 0.30 +
            extra * .25));
    }

    public void MoveObstacle(double screenX)
    {
        _obstaclePoint = HorseSpatial.ObstacleAt(screenX);
        alSource3f(_cue, Position, _obstaclePoint.X, _obstaclePoint.Y, _obstaclePoint.Z);
        // The pulse is spatial, but Doppler pitch-shifts short tones badly at
        // high speed. The continuous obstacle sound still receives Doppler.
        alSource3f(_cue, Velocity, 0, 0, 0);
    }

    public void BeginObstacleSound(int id, int sample, double screenX)
    {
        if (id <= 0) return;
        for (int i = 0; i < _obstacleBedIds.Length; i++)
            if (_obstacleBedIds[i] == id)
            {
                MoveObstacleSound(id, screenX);
                return;
            }
        int chosen = Math.Clamp(sample, 0, ObstacleSamples.Length - 1);
        int slot = Array.FindIndex(_obstacleBedIds, existing => existing == 0);
        if (slot < 0) slot = _nextObstacleBed++ % _obstacleBeds.Length;
        _obstacleBedIds[slot] = id;
        _obstacleBedTailing[slot] = false;
        _obstacleBedGain[slot] = ObstacleGains[chosen];
        SoundPoint point = HorseSpatial.ObstacleAt(screenX);
        Play(_obstacleBeds[slot], GetOggBuffer(ObstacleSamples[chosen]),
            point, _obstacleBedGain[slot], followsHorse: false);
        alSource3f(_obstacleBeds[slot], Velocity, ObstacleVelocityX, 0, 0);
        alSourcei(_obstacleBeds[slot], Looping, 1);
    }

    public void MoveObstacleSound(int id, double screenX)
    {
        for (int i = 0; i < _obstacleBedIds.Length; i++)
        {
            if (_obstacleBedIds[i] != id || _obstacleBedTailing[i]) continue;
            SoundPoint point = HorseSpatial.ObstacleAt(screenX);
            alSource3f(_obstacleBeds[i], Position, point.X, point.Y, point.Z);
            alSource3f(_obstacleBeds[i], Velocity, ObstacleVelocityX, 0, 0);
            return;
        }
    }

    public void EndObstacleSound(int id)
    {
        for (int i = 0; i < _obstacleBedIds.Length; i++)
        {
            if (_obstacleBedIds[i] != id) continue;
            _obstacleBedTailing[i] = true;
            _obstacleBedTailAge[i] = 0;
            return;
        }
    }

    public void StopObstacleSounds()
    {
        Array.Clear(_obstacleBedIds);
        Array.Fill(_obstacleBedTailing, false);
        foreach (uint source in _obstacleBeds)
            if (source != 0) alSourceStop(source);
    }

    public void DogVocal(int sample, double screenX)
    {
        uint source = _dogVoices[_nextDogVoice++ % _dogVoices.Length];
        Play(source, GetOggBuffer($"dog_voice_{Math.Clamp(sample, 0, 9) + 1:D2}"),
            HorseSpatial.DogAt(screenX), 0.62f, followsHorse: false);
        alSource3f(source, Velocity, _dogVelocityX, 0, 0);
    }

    public void DogStep(int sample, double screenX, DogFoot foot)
    {
        int slot = _nextDogStep++ % _dogSteps.Length;
        uint source = _dogSteps[slot];
        _dogStepFeet[slot] = foot;
        Play(source, GetOggBuffer($"dog_step_{Math.Clamp(sample, 0, 3) + 1:D2}"),
            HorseSpatial.DogPawAt(screenX, foot), 0.42f, followsHorse: false);
        alSource3f(source, Velocity, _dogVelocityX, 0, 0);
    }

    public void MoveDog(double screenX)
    {
        SoundPoint point = HorseSpatial.DogAt(screenX);
        _dogVelocityX = _dogPointValid ? Math.Clamp((point.X - _previousDogPoint.X) / (float)_motionDt, -3f, 3f) : 0;
        _previousDogPoint = point;
        _dogPointValid = true;
        foreach (uint source in _dogVoices)
        {
            alSource3f(source, Position, point.X, point.Y, point.Z);
            alSource3f(source, Velocity, _dogVelocityX, 0, 0);
        }
        for (int i = 0; i < _dogSteps.Length; i++)
        {
            SoundPoint paw = HorseSpatial.DogPawAt(screenX, _dogStepFeet[i]);
            alSource3f(_dogSteps[i], Position, paw.X, paw.Y, paw.Z);
            alSource3f(_dogSteps[i], Velocity, _dogVelocityX, 0, 0);
        }
    }

    public void DogBite(int sample, double screenX) =>
        Play(_dogBite, GetOggBuffer($"dog_bite_{Math.Clamp(sample, 0, 9) + 1:D2}"),
            HorseSpatial.DogAt(screenX), 0.90f, followsHorse: false);

    public void StopDog()
    {
        _dogPointValid = false;
        _dogVelocityX = 0;
        foreach (uint source in _dogVoices.Concat(_dogSteps))
            if (source != 0) alSourceStop(source);
    }

    private void PlayAmbientSlot(int slot)
    {
        int sample = _sampleRandom.Next(AmbientSamples.Length); // repeats are allowed
        float offset = (float)(_sampleRandom.NextDouble() * 35);
        Play(_ambient[slot], GetOggBuffer(AmbientSamples[sample]),
            new SoundPoint(0, 0, 0), 0, followsHorse: false, offsetSeconds: offset);
    }

    public void StartAmbient()
    {
        if (_ambientRunning) return;
        _ambientRunning = true;
        _ambientSlot = 0;
        _ambientIncoming = -1;
        _ambientAge = 0;
        _ambientCrossfadeAge = 0;
        _ambientSwitchAfter = 22 + (float)_sampleRandom.NextDouble() * 12;
        PlayAmbientSlot(_ambientSlot);
        Play(_wind, GetOggBuffer("wind_run"), new SoundPoint(0, 0, 0), 0, followsHorse: false);
        alSourcei(_wind, Looping, 1);
    }

    public void StopAmbient()
    {
        _ambientRunning = false;
        _ambientIncoming = -1;
        foreach (uint source in _ambient) if (source != 0) alSourceStop(source);
        alSourceStop(_wind);
        _windGain = _windWanted = 0;
    }

    public void StartFestival()
    {
        _festivalWanted = true;
        if (_festivalPlaying) return;
        _festivalPlaying = true;
        _festivalGain = 0;
        _festivalProgress = 0;
        _festivalPointValid = false;
        Play(_festivalMusic, GetOggBuffer("loop_folk_festivities"),
            HorseSpatial.FestivalAt(0), 0, followsHorse: false);
    }

    public void MoveFestival(double progress)
    {
        _festivalProgress = (float)Math.Clamp(progress, 0, 1);
        SoundPoint point = HorseSpatial.FestivalAt(_festivalProgress);
        alSource3f(_festivalMusic, Position, point.X, point.Y, point.Z);
        float vx = _festivalPointValid ? Math.Clamp((point.X - _previousFestivalPoint.X) / (float)_motionDt, -3f, 3f) : 0;
        alSource3f(_festivalMusic, Velocity, vx, 0, 0);
        _previousFestivalPoint = point;
        _festivalPointValid = true;
    }

    public void EndFestival() => _festivalWanted = false;

    public void UpdateBackground(double dt)
    {
        if (_ambientRunning)
        {
            float maxStep = (float)dt * 0.14f;
            _windGain += Math.Clamp(_windWanted - _windGain, -maxStep, maxStep);
            alSourcef(_wind, Gain, _windGain);
        }
        if (_festivalPlaying)
        {
            float delta = (float)dt * 0.35f;
            _festivalGain = Math.Clamp(_festivalGain + (_festivalWanted ? delta : -delta), 0, 0.42f);
            float proximity = 0.35f + 0.65f * MathF.Sin(MathF.PI * _festivalProgress);
            alSourcef(_festivalMusic, Gain, _festivalGain * proximity);
            if (!_festivalWanted && _festivalGain <= 0)
            {
                alSourceStop(_festivalMusic);
                _festivalPlaying = false;
            }
        }
        for (int i = 0; i < _obstacleBeds.Length; i++)
        {
            if (!_obstacleBedTailing[i]) continue;
            _obstacleBedTailAge[i] += (float)dt;
            float fade = Math.Max(0, 1 - _obstacleBedTailAge[i] / 0.65f);
            alSourcef(_obstacleBeds[i], Gain, _obstacleBedGain[i] * fade);
            if (fade > 0) continue;
            alSourceStop(_obstacleBeds[i]);
            _obstacleBedTailing[i] = false;
            _obstacleBedIds[i] = 0;
        }
        if (!_ambientRunning) return;
        _ambientAge += (float)dt;
        if (_ambientIncoming < 0 && _ambientAge >= _ambientSwitchAfter)
        {
            _ambientIncoming = 1 - _ambientSlot;
            _ambientCrossfadeAge = 0;
            PlayAmbientSlot(_ambientIncoming);
        }
        float baseGain = _festivalPlaying ? 0.075f : 0.22f;
        if (_ambientIncoming >= 0)
        {
            _ambientCrossfadeAge += (float)dt;
            float progress = Math.Min(1, _ambientCrossfadeAge / 2);
            alSourcef(_ambient[_ambientSlot], Gain, baseGain * (1 - progress));
            alSourcef(_ambient[_ambientIncoming], Gain, baseGain * progress);
            if (progress < 1) return;
            alSourceStop(_ambient[_ambientSlot]);
            _ambientSlot = _ambientIncoming;
            _ambientIncoming = -1;
            _ambientAge = 0;
            _ambientSwitchAfter = 22 + (float)_sampleRandom.NextDouble() * 12;
        }
        else alSourcef(_ambient[_ambientSlot], Gain,
            baseGain * Math.Min(1, _ambientAge / 1.5f));
    }

    public void Obstacle(int proximity, double screenX)
    {
        MoveObstacle(screenX);
        uint buffer = GetOggBuffer("obstacle-2");
        float progress = Math.Clamp(proximity / 1000f, 0, 1);
        float smooth = progress * progress * (3 - 2 * progress);
        Play(_cue, buffer, _obstaclePoint, 0.43f + smooth * 0.26f,
            pitch: 0.90f + smooth * 0.45f, followsHorse: false);
        alSource3f(_cue, Velocity, 0, 0, 0);
    }

    public void Jump()
    {
        uint buffer = GetOggBuffer("jump_neigh");
        _actionPoint = HorseSpatial.Head;
        Play(_action, buffer, _actionPoint, 0.65f);
    }

    public void Crash()
    {
        int pain = _sampleRandom.Next(_lastPain < 0 ? PainSamples.Length : PainSamples.Length - 1);
        if (_lastPain >= 0 && pain >= _lastPain) pain++;
        _lastPain = pain;
        Play(_vocal, GetOggBuffer(PainSamples[pain]), HorseSpatial.Head, 0.78f);
        uint buffer = GetOggBuffer("crash");
        _actionPoint = HorseSpatial.Body;
        Play(_action, buffer, _actionPoint, 0.9f);
    }

    public void StopAll()
    {
        _whipAge = WhipDuration;
        _festivalWanted = false;
        _festivalPlaying = false;
        _festivalGain = 0;
        _festivalPointValid = false;
        StopAmbient();
        foreach (uint source in _hooves)
            if (source != 0) alSourceStop(source);
        foreach (uint source in _saddles)
            if (source != 0) alSourceStop(source);
        foreach (uint source in _dust)
            if (source != 0) alSourceStop(source);
        StopObstacleSounds();
        StopDog();
        foreach (DustPuff puff in _dustPuffs) puff.Active = false;
        foreach (uint source in new[] { _cue, _action, _whip, _vocal, _festivalMusic, _dogBite, _wind })
            if (source != 0) alSourceStop(source);
    }

    public void Dispose()
    {
        StopAll();
        foreach (uint source in _hooves)
        {
            if (source == 0) continue;
            uint copy = source;
            alDeleteSources(1, ref copy);
        }
        foreach (uint source in _saddles)
        {
            if (source == 0) continue;
            uint copy = source;
            alDeleteSources(1, ref copy);
        }
        foreach (uint source in _dust)
        {
            if (source == 0) continue;
            uint copy = source;
            alDeleteSources(1, ref copy);
        }
        foreach (uint source in _ambient.Concat(_obstacleBeds).Concat(_dogVoices).Concat(_dogSteps))
        {
            if (source == 0) continue;
            uint copy = source;
            alDeleteSources(1, ref copy);
        }
        foreach (uint source in new[] { _cue, _action, _whip, _vocal, _festivalMusic, _dogBite, _wind })
        {
            if (source == 0) continue;
            uint copy = source;
            alDeleteSources(1, ref copy);
        }
        foreach (uint buffer in _buffers.Values)
        {
            uint copy = buffer;
            alDeleteBuffers(1, ref copy);
        }
        if (_context != IntPtr.Zero) { alcMakeContextCurrent(IntPtr.Zero); alcDestroyContext(_context); }
        if (_device != IntPtr.Zero) alcCloseDevice(_device);
    }
}
