using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Galope;

try
{
    bool noAudio = args.Contains("--no-audio", StringComparer.Ordinal);
    using IHorseAudio? audio = noAudio ? null : new HorseAudio();
    var game = new HorseGame(audio, line => Console.WriteLine(JsonSerializer.Serialize(
        new { op = "say", pt = line.PT, en = line.EN, es = line.ES })));
    bool platformVideo = Environment.GetEnvironmentVariable("R36S_VIDEO_OWNER") == "platform";
    bool graphicsEnabled = Environment.GetEnvironmentVariable("R36S_GRAPHICS") != "0";
    string videoSink = Environment.GetEnvironmentVariable("R36S_VIDEO_SINK") ?? "jsonl";
    using IFrameSink? frameSink = !platformVideo ? null : videoSink switch
    {
        "jsonl" => new JsonlFrameSink(),
        "files" => new FrameFileSink(Environment.GetEnvironmentVariable("R36S_FRAME_DIR") ??
            throw new InvalidOperationException("R36S_FRAME_DIR não foi definido.")),
        _ => throw new InvalidOperationException($"R36S_VIDEO_SINK inválido: {videoSink}")
    };
    using R36sFramebuffer? framebuffer = platformVideo ? null : R36sFramebuffer.TryOpen();
    string spriteDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "sprites");
    bool videoEnabled = platformVideo ? graphicsEnabled : framebuffer is not null;
    R36sScene? scene = videoEnabled ? new R36sScene(spriteDirectory) : null;
    var inputs = new ConcurrentQueue<InputEvent>();
    int closed = 0;
    _ = Task.Run(() =>
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("op", out var op) &&
                    op.GetString() == "settings")
                {
                    if (json.RootElement.TryGetProperty("graphics", out var graphics) &&
                        graphics.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        inputs.Enqueue(new InputEvent("", Graphics: graphics.GetBoolean()));
                    continue;
                }
                string action = json.RootElement.GetProperty("action").GetString() ?? "";
                string physical = json.RootElement.TryGetProperty("physical", out var p) ? p.GetString() ?? "" : "";
                inputs.Enqueue(new InputEvent(action, physical));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
                Console.Error.WriteLine("Evento inválido ignorado");
            }
        }
        Interlocked.Exchange(ref closed, 1);
    });
    game.Intro();
    var clock = Stopwatch.StartNew();
    var videoClock = Stopwatch.StartNew();
    bool suspended = false;
    while (!game.Exiting)
    {
        if (!SystemMenuGate.Available())
        {
            if (!suspended) { game.SuspendForSystemMenu(); suspended = true; }
            while (inputs.TryDequeue(out _)) { }
            Thread.Sleep(50);
            clock.Restart();
            continue;
        }
        if (suspended) { game.ResumeFromSystemMenu(); suspended = false; clock.Restart(); }
        double dt = clock.Elapsed.TotalSeconds;
        clock.Restart();
        for (int i = 0; i < 32 && inputs.TryDequeue(out var input); i++)
        {
            if (input.Graphics is bool enabled)
            {
                if (platformVideo && videoEnabled != enabled)
                {
                    videoEnabled = enabled;
                    scene = enabled ? new R36sScene(spriteDirectory) : null;
                    videoClock.Restart();
                }
            }
            else game.Handle(input);
        }
        if (Volatile.Read(ref closed) == 1 && inputs.IsEmpty) break;
        game.Tick(dt);
        if (videoEnabled && videoClock.ElapsedMilliseconds >= (platformVideo ? 66 : 40))
        {
            videoClock.Restart();
            try
            {
                scene!.Draw(game.View);
                if (frameSink is not null) frameSink.Present(scene.Pixels);
                else framebuffer!.Present(scene.Pixels);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Vídeo desativado após falha: {ex.Message}");
                videoEnabled = false; // audio and input remain available
            }
        }
        Thread.Sleep(15);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

internal static class SystemMenuGate
{
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
    private static FileStream? _file;
    internal static bool Available()
    {
        if (!OperatingSystem.IsLinux()) return true;
        string path = Environment.GetEnvironmentVariable("R36S_MENU_LOCK") ?? "/tmp/r36s-menu.lock";
        if (path == "none") return true;
        _file ??= new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        int fd = _file.SafeFileHandle.DangerousGetHandle().ToInt32();
        if (flock(fd, 1 | 4) != 0) return false; // LOCK_SH | LOCK_NB
        _ = flock(fd, 8); // LOCK_UN
        return true;
    }
}
