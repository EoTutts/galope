using System.Runtime.InteropServices;

namespace Galope;

// Linux fbdev output. Queries the existing mode and writes pixels only: it
// never changes video mode, backlight, console ownership or global settings.
internal sealed class R36sFramebuffer : IDisposable
{
    private const uint GetVariableInfo = 0x4600;
    private const uint GetFixedInfo = 0x4602;
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, IntPtr argument);

    private readonly FileStream _file;
    private readonly byte[] _frame;
    private readonly uint[] _palette = new uint[65536];
    private readonly int _width, _height, _pitch, _bytesPerPixel, _xOffset, _yOffset;
    private readonly int _drawX, _drawY, _drawWidth, _drawHeight;
    internal string Description => $"{_width}x{_height}, {_bytesPerPixel * 8} bpp";

    private R36sFramebuffer(FileStream file)
    {
        _file = file;
        IntPtr variable = Marshal.AllocHGlobal(160);
        IntPtr fixedInfo = Marshal.AllocHGlobal(80);
        try
        {
            int fd = file.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (ioctl(fd, GetVariableInfo, variable) != 0 ||
                ioctl(fd, GetFixedInfo, fixedInfo) != 0)
                throw new IOException("Não foi possível consultar o framebuffer.");
            _width = Marshal.ReadInt32(variable, 0);
            _height = Marshal.ReadInt32(variable, 4);
            int virtualWidth = Marshal.ReadInt32(variable, 8);
            int virtualHeight = Marshal.ReadInt32(variable, 12);
            _xOffset = Marshal.ReadInt32(variable, 16);
            _yOffset = Marshal.ReadInt32(variable, 20);
            int bpp = Marshal.ReadInt32(variable, 24);
            int grayscale = Marshal.ReadInt32(variable, 28);
            int redOffset = Marshal.ReadInt32(variable, 32);
            int redLength = Marshal.ReadInt32(variable, 36);
            int greenOffset = Marshal.ReadInt32(variable, 44);
            int greenLength = Marshal.ReadInt32(variable, 48);
            int blueOffset = Marshal.ReadInt32(variable, 56);
            int blueLength = Marshal.ReadInt32(variable, 60);
            int alphaOffset = Marshal.ReadInt32(variable, 68);
            int alphaLength = Marshal.ReadInt32(variable, 72);
            int redReversed = Marshal.ReadInt32(variable, 40);
            int greenReversed = Marshal.ReadInt32(variable, 52);
            int blueReversed = Marshal.ReadInt32(variable, 64);
            int memoryLength = Marshal.ReadInt32(fixedInfo, IntPtr.Size == 4 ? 20 : 24);
            int type = Marshal.ReadInt32(fixedInfo, IntPtr.Size == 4 ? 24 : 28);
            int visual = Marshal.ReadInt32(fixedInfo, IntPtr.Size == 4 ? 32 : 36);
            _pitch = Marshal.ReadInt32(fixedInfo, IntPtr.Size == 4 ? 44 : 48);
            _bytesPerPixel = bpp / 8;
            if (_width < 320 || _height < 180 || _width > 1920 || _height > 1200 ||
                _xOffset < 0 || _yOffset < 0 || _xOffset + _width > virtualWidth ||
                _yOffset + _height > virtualHeight ||
                bpp is not (16 or 24 or 32) || grayscale != 0 ||
                type != 0 || visual != 2 ||
                redReversed != 0 || greenReversed != 0 || blueReversed != 0 ||
                _pitch < (_xOffset + _width) * _bytesPerPixel ||
                !ValidField(redOffset, redLength, bpp) ||
                !ValidField(greenOffset, greenLength, bpp) ||
                !ValidField(blueOffset, blueLength, bpp) ||
                (alphaLength != 0 && !ValidField(alphaOffset, alphaLength, bpp)))
                throw new NotSupportedException("Formato do framebuffer não suportado.");
            long frameLength = (long)_pitch * (_yOffset + _height);
            if (frameLength > 8 * 1024 * 1024 || frameLength > memoryLength)
                throw new NotSupportedException("Framebuffer maior que o limite de memória do jogo.");
            _frame = new byte[frameLength];
            for (int rgb = 0; rgb < 65536; rgb++)
            {
                int red = ((rgb >> 11) & 31) * 255 / 31;
                int green = ((rgb >> 5) & 63) * 255 / 63;
                int blue = (rgb & 31) * 255 / 31;
                _palette[rgb] = Component(red, redOffset, redLength) |
                    Component(green, greenOffset, greenLength) |
                    Component(blue, blueOffset, blueLength) |
                    (alphaLength == 0 ? 0 : ((1u << alphaLength) - 1) << alphaOffset);
            }
            double scale = Math.Min(_width / (double)R36sScene.Width,
                _height / (double)R36sScene.Height);
            _drawWidth = (int)Math.Floor(R36sScene.Width * scale);
            _drawHeight = (int)Math.Floor(R36sScene.Height * scale);
            _drawX = (_width - _drawWidth) / 2;
            _drawY = (_height - _drawHeight) / 2;
        }
        finally
        {
            Marshal.FreeHGlobal(variable);
            Marshal.FreeHGlobal(fixedInfo);
        }
    }

    private static bool ValidField(int offset, int length, int bpp) =>
        offset >= 0 && length is > 0 and <= 16 && offset + length <= bpp;

    private static uint Component(int value, int offset, int length) =>
        (uint)((value * ((1 << length) - 1) + 127) / 255) << offset;

    internal static R36sFramebuffer? TryOpen()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("GALOPE_NO_VIDEO") == "1" ||
            Environment.GetEnvironmentVariable("R36S_GRAPHICS") == "0" ||
            Environment.GetEnvironmentVariable("R36S_VIDEO_OWNER") == "platform") return null;
        string path = Environment.GetEnvironmentVariable("GALOPE_FRAMEBUFFER") ?? "/dev/fb0";
        try
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite);
            try { return new R36sFramebuffer(file); }
            catch { file.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vídeo indisponível ({path}): {ex.Message}. Jogo segue por áudio.");
            return null;
        }
    }

    internal void Present(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != R36sScene.Width * R36sScene.Height)
            throw new ArgumentException("Tamanho de cena inválido.", nameof(pixels));
        for (int y = 0; y < _height; y++)
        {
            int drawY = y - _drawY;
            int sourceRow = drawY >= 0 && drawY < _drawHeight ?
                drawY * R36sScene.Height / _drawHeight * R36sScene.Width : -1;
            int rowStart = (y + _yOffset) * _pitch + _xOffset * _bytesPerPixel;
            for (int x = 0; x < _width; x++)
            {
                int drawX = x - _drawX;
                ushort rgb = sourceRow >= 0 && drawX >= 0 && drawX < _drawWidth ?
                    pixels[sourceRow + drawX * R36sScene.Width / _drawWidth] : (ushort)0;
                uint packed = _palette[rgb];
                int offset = rowStart + x * _bytesPerPixel;
                _frame[offset] = (byte)packed;
                _frame[offset + 1] = (byte)(packed >> 8);
                if (_bytesPerPixel >= 3) _frame[offset + 2] = (byte)(packed >> 16);
                if (_bytesPerPixel == 4) _frame[offset + 3] = (byte)(packed >> 24);
            }
        }
        _file.Position = 0;
        _file.Write(_frame);
    }

    public void Dispose()
    {
        try
        {
            Array.Clear(_frame);
            _file.Position = 0;
            _file.Write(_frame);
        }
        catch { /* Closing the game must not be blocked by a video error. */ }
        _file.Dispose();
    }
}
