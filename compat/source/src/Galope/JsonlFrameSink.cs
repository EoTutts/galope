using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Galope;

// Compact inline frames for the RK3326 platform's SDL renderer. The stream
// stays on stdout alongside speech; the hub is responsible for dropping old
// frames if it cannot render at the producer's rate.
internal sealed class JsonlFrameSink : IFrameSink
{
    private readonly MemoryStream _compressed = new();

    public void Present(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != R36sScene.Width * R36sScene.Height)
            throw new ArgumentException("Tamanho de cena inválido.", nameof(pixels));
        _compressed.SetLength(0);
        _compressed.Position = 0;
        using (var compressor = new ZLibStream(_compressed, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(MemoryMarshal.AsBytes(pixels));
        string encoded = Convert.ToBase64String(_compressed.GetBuffer(), 0,
            checked((int)_compressed.Length));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            op = "frame", width = R36sScene.Width, height = R36sScene.Height,
            encoding = "rgb565-zlib-base64", pixels = encoded
        }));
    }

    public void Dispose() => _compressed.Dispose();
}
