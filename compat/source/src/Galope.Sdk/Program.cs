using System.Diagnostics;
using System.Text.Json;
using Galope;
using R36S.A11y;

try
{
    // The existing hub and the SDK use different environment names. Keep the
    // same save file and the user's current language on either installation.
    string gameDir = Environment.GetEnvironmentVariable("A11Y_GAME_DIR") ??
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName ??
        AppContext.BaseDirectory;
    Environment.SetEnvironmentVariable("A11Y_GAME_DIR", gameDir);
    string saveDir = Environment.GetEnvironmentVariable("A11Y_SAVE_DIR") ??
        Environment.GetEnvironmentVariable("R36S_SAVE_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "galope-infinito");
    Environment.SetEnvironmentVariable("A11Y_SAVE_DIR", saveDir);
    Environment.SetEnvironmentVariable("R36S_SAVE_DIR", saveDir);
    if (Environment.GetEnvironmentVariable("A11Y_LANG") is null)
        Environment.SetEnvironmentVariable("A11Y_LANG", MenuSetting("language", "pt-BR"));
    bool sdkContract = Environment.GetEnvironmentVariable("A11Y_CONTRACT") == "1";
    if (!sdkContract && Environment.GetEnvironmentVariable("SPEECHD_ADDRESS") is null &&
        File.Exists("/tmp/r36s-speechd.sock"))
        Environment.SetEnvironmentVariable("SPEECHD_ADDRESS", "unix_socket:/tmp/r36s-speechd.sock");

    bool platformMode = !sdkContract && Environment.GetEnvironmentVariable("R36S_INPUT_PROFILE") is not null;
    // Galope needs precise Doppler, stereo ambience and on-demand sample
    // loading; keep its existing mixer and avoid opening a second SDK context.
    var sdk = new Game(new GameOptions { Fps = 60, Axes = true,
        EnableAudio = false, EnableInput = !platformMode });
    using IHorseAudio? audio = Environment.GetEnvironmentVariable("GALOPE_NO_AUDIO") == "1" ?
        null : new HorseAudio();
    string lastSpeech = "";
    var horse = new HorseGame(audio, line =>
        {
            lastSpeech = SpeechText(line, sdk.Lang);
            if (platformMode)
                Console.WriteLine(JsonSerializer.Serialize(new
                    { op = "say", pt = line.PT, en = line.EN, es = line.ES }));
            else sdk.Say(lastSpeech);
        },
        saveDir: sdk.SaveDir);
    bool platformVideo = !sdkContract && Environment.GetEnvironmentVariable("R36S_VIDEO_OWNER") == "platform";
    bool videoEnabled = sdkContract ? SystemGraphics() : Environment.GetEnvironmentVariable("R36S_GRAPHICS") != "0";
    string sinkName = Environment.GetEnvironmentVariable("R36S_VIDEO_SINK") ?? "jsonl";
    using IFrameSink? frameSink = !platformVideo ? null : sinkName switch
    {
        "jsonl" => new JsonlFrameSink(),
        "files" => new FrameFileSink(Environment.GetEnvironmentVariable("R36S_FRAME_DIR") ??
            throw new InvalidOperationException("R36S_FRAME_DIR não foi definido.")),
        _ => throw new InvalidOperationException($"R36S_VIDEO_SINK inválido: {sinkName}")
    };
    using SdlFrameSink? directVideo = sdkContract ? new SdlFrameSink() : null;
    using R36sFramebuffer? framebuffer = !sdkContract && !platformVideo && videoEnabled ?
        R36sFramebuffer.TryOpen() : null;
    videoEnabled &= sdkContract || platformVideo || framebuffer is not null;
    R36sScene? scene = videoEnabled ? new R36sScene(Path.Combine(AppContext.BaseDirectory,
        "assets", "sprites")) : null;
    var videoClock = Stopwatch.StartNew();
    var settingsClock = Stopwatch.StartNew();
    var axisDirection = new Dictionary<Axis, int>();

    if (platformMode)
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("op", out var op) && op.GetString() == "settings")
                    {
                        if (root.TryGetProperty("graphics", out var graphics) &&
                            graphics.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            bool enabled = graphics.GetBoolean();
                            sdk.Post(() =>
                            {
                                if (videoEnabled == enabled) return;
                                videoEnabled = enabled;
                                scene = enabled ? new R36sScene(Path.Combine(
                                    AppContext.BaseDirectory, "assets", "sprites")) : null;
                                videoClock.Restart();
                            });
                        }
                        continue;
                    }
                    string? action = root.TryGetProperty("action", out var value) ?
                        value.GetString() : null;
                    if (action is null) continue;
                    string physical = root.TryGetProperty("physical", out var body) ?
                        body.GetString() ?? "" : "";
                    sdk.Post(() =>
                    {
                        horse.Handle(new InputEvent(action, physical));
                        if (horse.Exiting) sdk.Exit();
                    });
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    Console.Error.WriteLine("Evento inválido ignorado");
                }
            }
            sdk.Post(sdk.Exit);
        });

    sdk.Start += horse.Intro;
    sdk.Button += e =>
    {
        if (platformMode) return;
        if (e is { Button: Button.Select, State: ButtonState.Pressed })
        {
            sdk.Say(lastSpeech);
            return;
        }
        if (SdkControls.Map(e, !sdkContract && IsSwitchLayout()) is { } input) horse.Handle(input);
        if (horse.Exiting) sdk.Exit();
    };
    sdk.Axis += e =>
    {
        if (platformMode) return;
        if (SdkControls.MapAxis(e.Axis, e.Value, axisDirection) is { } action)
            horse.Handle(new InputEvent(action));
        if (horse.Exiting) sdk.Exit();
    };
    sdk.Update += dt =>
    {
        horse.Tick(dt);
        if (horse.Exiting) { sdk.Exit(); return; }
        if (sdkContract && settingsClock.ElapsedMilliseconds >= 250)
        {
            settingsClock.Restart();
            bool enabled = SystemGraphics();
            if (enabled != videoEnabled)
            {
                videoEnabled = enabled;
                scene = enabled ? new R36sScene(Path.Combine(AppContext.BaseDirectory, "assets", "sprites")) : null;
                if (!enabled) directVideo!.Dispose();
                videoClock.Restart();
            }
        }
        if (!videoEnabled || videoClock.ElapsedMilliseconds < (platformVideo ? 66 : 40)) return;
        videoClock.Restart();
        try
        {
            scene!.Draw(horse.View);
            if (frameSink is not null) frameSink.Present(scene.Pixels);
            else if (directVideo is not null) directVideo.Present(scene.Pixels);
            else framebuffer!.Present(scene.Pixels);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vídeo desativado após falha: {ex.Message}");
            videoEnabled = false;
        }
    };
    sdk.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static string SpeechText(SpeechLine line, string language) => language switch
{
    "en-US" => line.EN,
    "es-ES" => line.ES,
    _ => line.PT
};

static bool IsSwitchLayout() => MenuSetting("layout", "xbox") == "switch";

static bool SystemGraphics()
{
    string path = Environment.GetEnvironmentVariable("A11Y_SISTEMA_CONF") ?? "/opt/a11y/etc/sistema.conf";
    try
    {
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == "SCREEN")
                return parts[1].Trim().Trim('\'', '"') != "0";
        }
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    return true;
}

static string MenuSetting(string key, string fallback)
{
    string? overrideValue = Environment.GetEnvironmentVariable(
        key == "layout" ? "R36S_LAYOUT" : "R36S_LANGUAGE");
    if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue;
    const string menuConfig = "/home/player/.config/r36s/menu.conf";
    try
    {
        foreach (string line in File.ReadLines(menuConfig))
        {
            string[] parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == key) return parts[1].Trim();
        }
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    return fallback;
}
