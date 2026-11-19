namespace Galope;

internal readonly record struct SkyColor(byte R, byte G, byte B)
{
    internal static SkyColor Lerp(SkyColor a, SkyColor b, double t) => new(
        (byte)Math.Clamp((int)Math.Round(a.R + (b.R - a.R) * t), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.G + (b.G - a.G) * t), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.B + (b.B - a.B) * t), 0, 255));
}

internal readonly record struct SkyView(SkyColor Top, SkyColor Horizon,
    bool SunVisible, bool MoonVisible, double BodyX, double BodyY,
    double StarStrength);

internal static class SkyCycle
{
    internal const double SecondsPerCycle = 120;
    private readonly record struct Key(double Phase, SkyColor Top, SkyColor Horizon);
    private static readonly Key[] Keys = [
        new(0, new(33, 33, 77), new(179, 92, 94)),
        new(.12, new(68, 150, 210), new(169, 218, 239)),
        new(.30, new(57, 153, 215), new(171, 218, 234)),
        new(.47, new(96, 91, 153), new(230, 132, 84)),
        new(.55, new(23, 29, 65), new(79, 72, 116)),
        new(.65, new(9, 15, 40), new(24, 35, 64)),
        new(.85, new(9, 15, 40), new(24, 35, 64)),
        new(1, new(33, 33, 77), new(179, 92, 94))
    ];

    internal static SkyView At(double phase)
    {
        phase -= Math.Floor(phase);
        Key first = Keys[0], last = Keys[^1];
        for (int i = 1; i < Keys.Length; i++)
            if (phase <= Keys[i].Phase)
            {
                first = Keys[i - 1];
                last = Keys[i];
                break;
            }
        double blend = (phase - first.Phase) / (last.Phase - first.Phase);
        bool sun = phase < .55;
        double travel = sun ? phase / .55 : (phase - .55) / .45;
        double x = 884 - 808 * travel;
        double y = 228 - 134 * Math.Sin(Math.PI * travel);
        double stars = Math.Clamp((phase - .56) / .10, 0, 1) *
            Math.Clamp((1 - phase) / .10, 0, 1);
        return new SkyView(SkyColor.Lerp(first.Top, last.Top, blend),
            SkyColor.Lerp(first.Horizon, last.Horizon, blend),
            sun, !sun, x, y, stars);
    }
}
