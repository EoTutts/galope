using System.Text;

namespace Galope;

// Small RGB565 scene for the R36S. Sprites are rasterized at build time and
// loaded on first use; no System.Drawing, PNG decoder or window server is needed.
internal sealed class R36sScene
{
    internal const int Width = 640;
    internal const int Height = 360;
    private const double Scale = 2.0 / 3.0;
    private const int RoadY = 267;
    private readonly ushort[] _pixels = new ushort[Width * Height];
    private static readonly ushort[] Gray565 = Enumerable.Range(0, 256)
        .Select(value => Pack(value, value, value)).ToArray();
    private readonly Dictionary<string, R36sSprite> _sprites = [];
    private readonly string _assetDirectory;
    internal ReadOnlySpan<ushort> Pixels => _pixels;

    internal R36sScene(string assetDirectory) => _assetDirectory = assetDirectory;

    internal void Draw(HorseView view)
    {
        DrawSky(view.DayPhase);
        DrawLandscape(view.Distance);
        DrawRoad(view.Distance);
        if (view.Festival != FestivalPhase.Waiting)
        {
            int fairFrame = (int)(view.DayPhase * SkyCycle.SecondsPerCycle * 4) & 3;
            R36sSprite fair = Sprite($"festival-{fairFrame}");
            int movement = (int)Math.Round((.5 - view.FestivalTraversal) * 1300 * Scale);
            Blit(fair, fair.Left + movement, fair.Top);
        }
        foreach (ObstacleSpec obstacle in view.Obstacles)
        {
            if (view.Dog.Phase != DogPhase.None && obstacle.AudioId == view.Dog.AudioId)
                continue;
            int x = (int)Math.Round((HorseGame.HorseFrontX +
                (obstacle.Start - view.Distance) * HorseGame.PixelsPerUnit) * Scale);
            int width = (int)Math.Round(obstacle.Width * HorseGame.PixelsPerUnit * Scale);
            int height = (int)Math.Round(obstacle.Height * 96 * Scale);
            if (x + width < 0 || x >= Width) continue;
            double seconds = view.DayPhase * SkyCycle.SecondsPerCycle;
            string name = obstacle.Kind == ObstacleKind.Pitbull ?
                $"pitbull-idle-{HorseGame.DogVisualFrame(seconds, obstacle.Start, false)}" :
                $"obstacle-{obstacle.SoundSample}-{HorseGame.VisualFrame(
                    obstacle.SoundSample, view.Distance, seconds, obstacle.Start)}";
            BlitScaled(Sprite(name), x, RoadY - height, width, height);
        }
        if (view.Dog.Phase != DogPhase.None)
        {
            int center = (int)Math.Round(view.Dog.ScreenX * Scale);
            if (center is > -40 and < 680)
            {
                int frame = HorseGame.DogVisualFrame(view.DayPhase * SkyCycle.SecondsPerCycle,
                    view.Dog.AudioId, view.Dog.Phase == DogPhase.Chasing);
                BlitScaled(Sprite($"pitbull-{(view.Dog.Phase == DogPhase.Chasing ? "run" : "idle")}-{frame}"),
                    center - 12, RoadY - 27, 24, 27);
            }
        }
        string horseSprite;
        if (view.JumpProgress >= 0)
            horseSprite = $"jump-{JumpFrame(view.JumpProgress)}";
        else if (view.WhipProgress >= 0)
            horseSprite = $"whip-{Math.Min(3, (int)(view.WhipProgress * 4))}";
        else
            horseSprite = $"horse-{Math.Clamp(view.GaitSpeed, 1, 5)}-" +
                $"{Math.Min(3, (int)(view.GaitPhase * 4))}";
        R36sSprite horse = Sprite(horseSprite);
        Blit(horse, horse.Left, horse.Top);
        DrawHud(view);
        if (view.Screen != "Racing") DrawMenu(view);
    }

    private static int JumpFrame(double progress) => progress switch
    {
        < .16 => 0, < .38 => 1, < .63 => 2, < .86 => 3, _ => 4
    };

    private static ushort Pack(int red, int green, int blue) =>
        (ushort)(((red * 31 / 255) << 11) | ((green * 63 / 255) << 5) |
            (blue * 31 / 255));

    private void DrawSky(double phase)
    {
        SkyView sky = SkyCycle.At(phase);
        // Match the Windows canvas: the gradient stops at world y=370.
        // Everything below it starts black, including the small gap above
        // the road. Filling the whole framebuffer leaked sky under the fence.
        _pixels.AsSpan().Fill(0);
        const int skyHeight = 247; // 370 * 2/3, rounded up
        for (int y = 0; y < skyHeight; y++)
        {
            SkyColor c = SkyColor.Lerp(sky.Top, sky.Horizon,
                Math.Clamp(y / 247.0, 0, 1));
            _pixels.AsSpan(y * Width, Width).Fill(Pack(c.R, c.G, c.B));
        }
        if (sky.StarStrength > 0)
        {
            ushort star = Pack((int)(235 * sky.StarStrength),
                (int)(235 * sky.StarStrength), (int)(222 * sky.StarStrength));
            for (int i = 0; i < 34; i++)
            {
                int x = ((i * 223 + 89) % 938 + 11) * 2 / 3;
                int y = ((i * 137 + 29) % 248 + 18) * 2 / 3;
                RectColor(x, y, i % 7 == 0 ? 2 : 1, i % 7 == 0 ? 2 : 1, star);
            }
        }
        int cx = (int)(sky.BodyX * Scale), cy = (int)(sky.BodyY * Scale);
        int radius = sky.SunVisible ? 15 : 13;
        if (sky.SunVisible) DrawSunHalo(cx, cy);
        ushort body = sky.SunVisible ? Pack(255, 238, 176) : Pack(227, 232, 219);
        for (int dy = -radius; dy <= radius; dy++)
        {
            int dx = (int)Math.Sqrt(radius * radius - dy * dy);
            RectColor(cx - dx, cy + dy, dx * 2 + 1, 1, body);
        }
        if (sky.MoonVisible)
        {
            RectColor(cx - 5, cy - 6, 4, 3, Pack(180, 187, 183));
            RectColor(cx + 3, cy + 2, 3, 4, Pack(180, 187, 183));
        }
    }

    private void DrawSunHalo(int cx, int cy)
    {
        const int radius = 23;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y is < 0 or >= Height) continue;
            int dx = (int)Math.Sqrt(radius * radius - dy * dy);
            for (int x = Math.Max(0, cx - dx); x <= Math.Min(Width - 1, cx + dx); x++)
            {
                ushort old = _pixels[y * Width + x];
                int red = ((old >> 11) & 31) * 255 / 31;
                int green = ((old >> 5) & 63) * 255 / 63;
                int blue = (old & 31) * 255 / 31;
                _pixels[y * Width + x] = Pack((red * 200 + 255 * 55) / 255,
                    (green * 200 + 211 * 55) / 255,
                    (blue * 200 + 112 * 55) / 255);
            }
        }
    }

    private R36sSprite Sprite(string name)
    {
        if (_sprites.TryGetValue(name, out R36sSprite? sprite)) return sprite;
        byte[] data = File.ReadAllBytes(Path.Combine(_assetDirectory, name + ".gsp"));
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "GSP1")
            throw new InvalidDataException($"Sprite inválido: {name}");
        int width = BitConverter.ToUInt16(data, 4);
        int height = BitConverter.ToUInt16(data, 6);
        if (width <= 0 || height <= 0 || data.Length != 12 + width * height)
            throw new InvalidDataException($"Dimensões inválidas: {name}");
        sprite = new R36sSprite(width, height, BitConverter.ToInt16(data, 8),
            BitConverter.ToInt16(data, 10), data[12..]);
        _sprites[name] = sprite;
        return sprite;
    }

    private void DrawLandscape(double distance)
    {
        float hillShift = (float)(distance * .24 % 240);
        for (int x = -32; x < 992; x += 16)
        {
            int h = 326 + (int)(9 * Math.Sin((x + hillShift) * .013) +
                5 * Math.Sin((x + hillShift) * .041));
            WinRect(x, h, 16, 371 - h, 31);
        }
        float farScroll = (float)(distance * .62);
        int first = (int)Math.Floor(farScroll / 155) - 1;
        for (int i = first; i < first + 9; i++)
        {
            float x = i * 155 - farScroll;
            switch (PositiveMod(i, 6))
            {
                case 0: DrawFarmhouse(x + 17, 337); break;
                case 1: DrawFarTree(x + 55, 355, 1); DrawFarTree(x + 99, 359, 0); break;
                case 2: DrawBarn(x + 20, 344); break;
                case 3: DrawFarTree(x + 45, 356, 2); break;
                case 4: DrawSilo(x + 34, 350); DrawFarTree(x + 93, 357, 0); break;
                default: DrawFarmhouse(x + 12, 342); DrawFarTree(x + 114, 357, 1); break;
            }
        }
        float nearScroll = (float)(distance * 2.8);
        int firstNear = (int)Math.Floor(nearScroll / 330) - 1;
        for (int i = firstNear; i < firstNear + 5; i++)
            DrawRoadsideTree(i * 330 - nearScroll + 90, 395, PositiveMod(i, 3));
        WinRect(0, 370, 960, 4, 55);
        float fence = (float)(distance * 1.8 % 58);
        for (float x = -58 - fence; x < 1000; x += 58)
        {
            WinRect(x, 377, 3, 18, 86);
            WinRect(x, 382, 58, 2, 55);
            WinRect(x, 389, 58, 2, 55);
        }
    }

    private static int PositiveMod(int value, int modulus) => (value % modulus + modulus) % modulus;

    private void WinRect(float x, float y, float width, float height, byte color)
    {
        int x0 = (int)Math.Round(x * Scale), y0 = (int)Math.Round(y * Scale);
        int x1 = (int)Math.Round((x + width) * Scale);
        int y1 = (int)Math.Round((y + height) * Scale);
        Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0), color);
    }

    private void DrawFarmhouse(float x, int bottom)
    {
        WinRect(x + 8, bottom - 22, 41, 22, 55);
        WinRect(x + 4, bottom - 25, 49, 4, 86);
        WinRect(x + 10, bottom - 29, 37, 4, 86);
        WinRect(x + 18, bottom - 33, 21, 4, 86);
        WinRect(x + 16, bottom - 15, 6, 6, 0);
        WinRect(x + 34, bottom - 15, 6, 6, 0);
        WinRect(x + 26, bottom - 12, 5, 12, 31);
    }

    private void DrawBarn(float x, int bottom)
    {
        WinRect(x + 4, bottom - 24, 54, 24, 55);
        WinRect(x, bottom - 28, 62, 4, 86);
        WinRect(x + 7, bottom - 33, 48, 5, 86);
        WinRect(x + 18, bottom - 38, 26, 5, 86);
        WinRect(x + 25, bottom - 19, 12, 19, 31);
        WinRect(x + 30, bottom - 19, 2, 19, 86);
    }

    private void DrawSilo(float x, int bottom)
    {
        WinRect(x + 7, bottom - 44, 22, 44, 55);
        WinRect(x + 4, bottom - 47, 28, 4, 86);
        WinRect(x + 9, bottom - 51, 18, 4, 86);
        WinRect(x + 18, bottom - 37, 4, 5, 31);
    }

    private void DrawFarTree(float x, int bottom, int variant)
    {
        int tall = variant == 2 ? 54 : variant == 1 ? 42 : 33;
        WinRect(x + 12, bottom - tall + 12, 5, tall - 12, 55);
        WinRect(x + 4, bottom - tall + 20, 21, 9, 86);
        WinRect(x, bottom - tall + 15, 29, 7, 86);
        WinRect(x + 4, bottom - tall + 8, 21, 8, 86);
        WinRect(x + 9, bottom - tall + 3, 11, 7, 86);
    }

    private void DrawRoadsideTree(float x, int bottom, int variant)
    {
        int height = 75 + variant * 10;
        WinRect(x + 20, bottom - height + 31, 7, height - 31, 55);
        WinRect(x + 10, bottom - height + 34, 28, 14, 86);
        WinRect(x + 3, bottom - height + 23, 42, 15, 86);
        WinRect(x + 9, bottom - height + 12, 31, 14, 86);
        WinRect(x + 16, bottom - height + 4, 17, 11, 86);
    }

    private void DrawRoad(double distance)
    {
        WinRect(0, 403, 960, 137, 18);
        WinRect(0, 398, 960, 3, 255);
        WinRect(0, 412, 960, 2, 55);
        float scroll = (float)(distance * HorseGame.PixelsPerUnit % 108);
        for (float x = -108 - scroll; x < 960; x += 108)
        {
            WinRect(x, 453, 43, 4, 86);
            WinRect(x + 66, 419, 16, 2, 31);
        }
        WinRect(0, 477, 960, 2, 31);
    }

    private void DrawHud(HorseView view)
    {
        Blit(Sprite("logo"), 17, 13);
        Text($"RITMO {HorseGame.PaceLabel(view.ActualSpeed)}", 19, 49, 1);
        Text($"{(int)view.Distance} M", 151, 49, 1);
        Text($"OBST {view.Cleared}", 277, 49, 1);
        Text($"REC {view.BestDistance} M", 433, 49, 1);
        if (view.Festival != FestivalPhase.Waiting)
            Text(view.Festival == FestivalPhase.Warning ?
                "FESTIVAL A FRENTE  DESACELERANDO" : "FESTIVAL  RITMO 2", 19, 70, 1);
        if (view.Dog.Phase == DogPhase.Chasing)
            Text("CAO PERSEGUINDO  ACELERANDO", 19, 88, 1);
        if (view.Screen == "Racing") Text("B SALTA   START PAUSA", 53, 324, 1);
    }

    private void DrawMenu(HorseView view)
    {
        Rect(174, 80, 292, 215, 0);
        Rect(174, 80, 292, 2, 255); Rect(174, 293, 292, 2, 255);
        Rect(174, 80, 2, 215, 255); Rect(464, 80, 2, 215, 255);
        string title = view.Screen switch
        {
            "Menu" => "MENU", "Instructions" => "COMO JOGAR",
            "Paused" => "PAUSA", "GameOver" => view.Bitten ? "MORDIDA" : "COLISAO", _ => ""
        };
        if (view.Screen == "Menu")
        {
            R36sSprite logo = Sprite("logo");
            Blit(logo, (Width - logo.Width) / 2, 100);
        }
        else Text(title, 195, 101, 2);
        if (view.Screen == "Instructions")
        {
            Text("CAVALO CORRE SEM PARAR", 195, 148, 1);
            Text("B SALTA", 195, 166, 1);
            Text("ACELERA A CADA OBSTACULO", 195, 184, 1);
            Text("FESTIVAL: RITMO 2", 195, 202, 1);
            return;
        }
        string[] items = view.Screen switch
        {
            "Menu" => ["JOGAR", "INSTRUCOES", "SAIR"],
            "Paused" => ["CONTINUAR", "RECOMECAR", "SAIR"],
            _ => ["JOGAR NOVAMENTE", "SAIR"]
        };
        for (int i = 0; i < items.Length; i++)
        {
            if (i == view.Selection) Text(">", 195, 154 + i * 31, 2);
            Text(items[i], 219, 154 + i * 31, 2);
        }
    }

    private void Rect(int x, int y, int width, int height, byte color)
        => RectColor(x, y, width, height, Gray565[color]);

    private void RectColor(int x, int y, int width, int height, ushort color)
    {
        if (width <= 0 || height <= 0) return;
        int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + width);
        int y0 = Math.Max(0, y), y1 = Math.Min(Height, y + height);
        if (x1 <= x0 || y1 <= y0) return;
        for (int row = y0; row < y1; row++)
            _pixels.AsSpan(row * Width + x0, x1 - x0).Fill(color);
    }

    private void Blit(R36sSprite sprite, int x, int y)
    {
        int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + sprite.Width);
        int y0 = Math.Max(0, y), y1 = Math.Min(Height, y + sprite.Height);
        for (int row = y0; row < y1; row++)
            for (int col = x0; col < x1; col++)
            {
                byte pixel = sprite.Pixels[(row - y) * sprite.Width + col - x];
                if (pixel != 0) _pixels[row * Width + col] = Gray565[pixel];
            }
    }

    private void BlitScaled(R36sSprite sprite, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + width);
        int y0 = Math.Max(0, y), y1 = Math.Min(Height, y + height);
        for (int row = y0; row < y1; row++)
        {
            int sourceY = (row - y) * sprite.Height / height;
            for (int col = x0; col < x1; col++)
            {
                int sourceX = (col - x) * sprite.Width / width;
                byte pixel = sprite.Pixels[sourceY * sprite.Width + sourceX];
                if (pixel != 0) _pixels[row * Width + col] = Gray565[pixel];
            }
        }
    }

    private void Text(string text, int x, int y, int scale)
    {
        foreach (char ch in text.ToUpperInvariant())
        {
            ReadOnlySpan<byte> rows = PixelFont.Glyph(ch);
            int top = y + (9 - rows.Length) * scale;
            for (int row = 0; row < rows.Length; row++)
                for (int col = 0; col < 5; col++)
                    if ((rows[row] & (1 << (4 - col))) != 0)
                        Rect(x + col * scale, top + row * scale, scale, scale, 255);
            x += 6 * scale;
        }
    }

}

internal sealed record R36sSprite(int Width, int Height, int Left, int Top, byte[] Pixels);
