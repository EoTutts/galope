namespace Galope;

internal interface IFrameSink : IDisposable
{
    void Present(ReadOnlySpan<ushort> pixels);
}
