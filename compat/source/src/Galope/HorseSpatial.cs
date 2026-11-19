namespace Galope;

internal readonly record struct SoundPoint(float X, float Y, float Z);

// The runner stays at a fixed screen position while the world moves past him.
// Audio uses the same camera frame: the listener is at screen center and the
// horse remains at screen center, with small offsets between its body parts.
internal static class HorseSpatial
{
    private const float ScreenCenter = 480;
    private const float PixelsPerAudioUnit = 660;
    internal static readonly float HorseX =
        ((HorseGame.HorseRearX + HorseGame.HorseFrontX) / 2 - ScreenCenter) / PixelsPerAudioUnit;

    internal static SoundPoint Hoof(HoofFoot foot)
    {
        bool front = foot is HoofFoot.FrontLeft or HoofFoot.FrontRight;
        bool left = foot is HoofFoot.HindLeft or HoofFoot.FrontLeft;
        return new(HorseX + (front ? 0.18f : -0.18f) + (left ? -0.055f : 0.055f),
            -0.52f, left ? -2.18f : -2.32f);
    }

    internal static SoundPoint Rider => new(HorseX, 0.04f, -2.20f);
    internal static SoundPoint WhipAt(float progress)
    {
        float t = Math.Clamp(progress, 0, 1);
        return new(HorseX - 0.20f - 0.85f * t,
            0.25f + 0.28f * MathF.Sin(MathF.PI * t) - 0.30f * t,
            -2.20f - 0.16f * t);
    }
    internal static SoundPoint Head => new(HorseX + 0.30f, 0.42f, -2.22f);
    internal static SoundPoint Body => new(HorseX, -0.14f, -2.24f);
    internal static SoundPoint Background => new(0, -0.05f, -3.1f);
    internal static SoundPoint FestivalAt(double progress)
    {
        float t = (float)Math.Clamp(progress, 0, 1);
        return new(2.2f - 4.4f * t, 0.02f,
            -4.0f + 1.6f * MathF.Sin(MathF.PI * t));
    }
    internal static SoundPoint DustAt(HoofFoot foot, double travelled, double ageFraction)
    {
        float side = foot == HoofFoot.HindLeft ? -0.055f : 0.055f;
        return new(HorseX - 0.30f + side - (float)(travelled * HorseGame.PixelsPerUnit / 440),
            -0.66f + (float)(Math.Clamp(ageFraction, 0, 1) * 0.10),
            -2.30f - (float)(Math.Clamp(ageFraction, 0, 1) * 0.12));
    }

    internal static float DustGain(double speed) =>
        (float)(0.035 + 0.06 * Math.Clamp((speed - 12.07) / (24.73 - 12.07), 0, 1));

    internal static float DustFade(double ageFraction)
    {
        double progress = Math.Clamp(ageFraction, 0, 1);
        return (float)(Math.Min(1, progress / 0.025) * (1 - progress));
    }

    internal static SoundPoint ObstacleAt(double screenX) =>
        new(Math.Clamp((float)((screenX - ScreenCenter) / 440), -8.0f, 8.0f),
            -0.30f, -3.10f);

    internal static SoundPoint DogAt(double screenX)
    {
        float distance = Math.Clamp((float)Math.Abs(screenX - HorseGame.HorseRearX) / 420, 0, 1);
        return new(Math.Clamp((float)((screenX - ScreenCenter) / 370), -2.25f, 2.25f),
            -0.55f, -2.25f - distance * 1.35f);
    }

    internal static SoundPoint DogPawAt(double screenX, DogFoot foot)
    {
        SoundPoint body = DogAt(screenX);
        bool front = foot is DogFoot.FrontLeft or DogFoot.FrontRight;
        bool left = foot is DogFoot.FrontLeft or DogFoot.HindLeft;
        return new(body.X + (front ? 0.07f : -0.07f) + (left ? -0.025f : 0.025f),
            -0.68f, body.Z + (left ? 0.025f : -0.025f));
    }
}
