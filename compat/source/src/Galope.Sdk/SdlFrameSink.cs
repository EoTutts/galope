using System.Runtime.InteropServices;
using Galope;

// SDL owns only the game's window. Fn/backlight remain with a11yd.
internal sealed class SdlFrameSink : IFrameSink
{
    private const string Lib = "libSDL2-2.0.so.0";
    private IntPtr window, renderer, texture;
    private readonly byte[] events = new byte[64];
    private static void Check(int value)
    {
        if (value < 0) throw new IOException(Marshal.PtrToStringUTF8(SDL_GetError()));
    }
    private static IntPtr Required(IntPtr value)
    {
        if (value == IntPtr.Zero) throw new IOException(Marshal.PtrToStringUTF8(SDL_GetError()));
        return value;
    }
    public void Present(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != R36sScene.Width * R36sScene.Height)
            throw new ArgumentException("Invalid RGB565 frame");
        if (window == IntPtr.Zero)
        {
            try
            {
                Check(SDL_InitSubSystem(0x20));
                window = Required(SDL_CreateWindow("Galope", 0, 0, 640, 480, 1));
                renderer = Required(SDL_CreateRenderer(window, -1, 1));
                Check(SDL_RenderSetLogicalSize(renderer, 640, 480));
                texture = Required(SDL_CreateTexture(renderer, 0x15151002, 1,
                    R36sScene.Width, R36sScene.Height));
            }
            catch { Dispose(); throw; }
        }
        while (SDL_PollEvent(events) != 0) { }
        // SDL copies pixels during UpdateTexture; no unmanaged cache survives.
        byte[] bytes = MemoryMarshal.AsBytes(pixels).ToArray();
        Check(SDL_UpdateTexture(texture, IntPtr.Zero, bytes, R36sScene.Width * 2));
        Check(SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255));
        Check(SDL_RenderClear(renderer));
        var target = new Rect { x = 0, y = 60, w = 640, h = 360 };
        Check(SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref target));
        SDL_RenderPresent(renderer);
    }
    public void Dispose()
    {
        if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
        if (renderer != IntPtr.Zero) SDL_DestroyRenderer(renderer);
        if (window != IntPtr.Zero) SDL_DestroyWindow(window);
        texture = renderer = window = IntPtr.Zero;
        SDL_QuitSubSystem(0x20);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int x, y, w, h; }
    [DllImport(Lib)] private static extern int SDL_InitSubSystem(uint flags);
    [DllImport(Lib)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Lib)] private static extern IntPtr SDL_GetError();
    [DllImport(Lib)] private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);
    [DllImport(Lib)] private static extern void SDL_DestroyWindow(IntPtr p);
    [DllImport(Lib)] private static extern IntPtr SDL_CreateRenderer(IntPtr p, int index, uint flags);
    [DllImport(Lib)] private static extern void SDL_DestroyRenderer(IntPtr p);
    [DllImport(Lib)] private static extern int SDL_RenderSetLogicalSize(IntPtr p, int w, int h);
    [DllImport(Lib)] private static extern IntPtr SDL_CreateTexture(IntPtr p, uint format, int access, int w, int h);
    [DllImport(Lib)] private static extern void SDL_DestroyTexture(IntPtr p);
    [DllImport(Lib)] private static extern int SDL_UpdateTexture(IntPtr p, IntPtr rect, byte[] pixels, int pitch);
    [DllImport(Lib)] private static extern int SDL_SetRenderDrawColor(IntPtr p, byte r, byte g, byte b, byte a);
    [DllImport(Lib)] private static extern int SDL_RenderClear(IntPtr p);
    [DllImport(Lib)] private static extern int SDL_RenderCopy(IntPtr p, IntPtr t, IntPtr src, ref Rect dst);
    [DllImport(Lib)] private static extern void SDL_RenderPresent(IntPtr p);
    [DllImport(Lib)] private static extern int SDL_PollEvent(byte[] p);
}
