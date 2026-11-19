using System.Runtime.InteropServices;
using System.Text.Json;

namespace Galope;

// The platform owns SDL and reads complete RGB565LE frames from its session
// directory. Atomic replacement keeps the renderer from seeing a partial file.
internal sealed class FrameFileSink : IFrameSink
{
    private readonly string _directory;
    private int _slot;

    internal FrameFileSink(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("R36S_FRAME_DIR precisa ser absoluto.", nameof(directory));
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Present(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != R36sScene.Width * R36sScene.Height)
            throw new ArgumentException("Tamanho de cena inválido.", nameof(pixels));
        string name = $"frame-{_slot}.rgb565";
        string target = Path.Combine(_directory, name);
        string pending = target + ".tmp";
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            file.Write(MemoryMarshal.AsBytes(pixels));
        File.Move(pending, target, overwrite: true);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            op = "frame", path = name, format = "rgb565le",
            width = R36sScene.Width, height = R36sScene.Height
        }));
        _slot ^= 1;
    }

    public void Dispose() { }
}
