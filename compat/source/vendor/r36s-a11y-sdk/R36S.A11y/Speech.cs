using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace R36S.A11y;

/// <summary>
/// Fala pelo Speech Dispatcher (protocolo SSIP no socket Unix).
/// Usa a seção voice do config.json do Luma, com fallback legado; fala
/// interrompe a anterior por padrão. Sem Speech Dispatcher (no PC, por
/// exemplo), a fala vai para o stderr.
/// </summary>
public sealed class Speech
{
    private readonly string client;
    private string language;
    private readonly Action<Action> post; // roda o callback na thread do jogo
    private Socket? socket;
    private BlockingCollection<string[]>? replies;
    private bool available = true;
    private readonly object sync = new();
    private readonly Dictionary<string, Action<bool>> callbacks = new();
    private readonly Dictionary<string, bool> early = new(); // terminaram antes do callback
    private string? appliedVoice;

    internal Speech(string client, string language, Action<Action> post)
    {
        this.client = client;
        this.language = language;
        this.post = post;
    }

    /// <summary>Idioma desta fala; as preferências de voz continuam sendo do usuário.</summary>
    public void SetLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Idioma inválido", nameof(value));
        if (language == value) return;
        language = value;
        appliedVoice = null;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();

    private static string SocketPath()
    {
        string? address = Environment.GetEnvironmentVariable("SPEECHD_ADDRESS");
        if (address?.StartsWith("unix_socket:", StringComparison.Ordinal) == true)
            return address["unix_socket:".Length..];
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = "/run/user/" + getuid();
        return Path.Combine(runtime, "speech-dispatcher", "speechd.sock");
    }

    /// <summary>Voz do usuário: RATE, PITCH, VOLUME (0..100 → -100..100), VOICE_MODULE e VOICE.</summary>
    internal static (Dictionary<string, int> Numbers, Dictionary<string, string> Names) UserVoice()
    {
        var configured = Environment.GetEnvironmentVariable("A11Y_CONFIG") ?? "/roms/a11y/config.json";
        var path = Environment.GetEnvironmentVariable("A11Y_VOZ_CONF") ??
            (File.Exists(configured) ? configured : "/opt/a11y/etc/voz.conf");
        var numbers = new Dictionary<string, int>();
        var names = new Dictionary<string, string>();
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception) { return (numbers, names); }
        var values = new Dictionary<string, string>();
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object)
                    foreach (var field in voice.EnumerateObject())
                        if (field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            values[field.Name] = field.Value.ToString();
            }
            catch (JsonException) { return (numbers, names); }
        }
        else foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('\'', '"');
        }
        foreach (var (key, val) in values)
        {
            if (key is "VOICE_MODULE" or "VOICE")
            {
                if (val.Length > 0) names[key] = val;
            }
            else if (key is "RATE" or "PITCH" or "VOLUME" && int.TryParse(val, out int n))
            {
                numbers[key] = Math.Clamp(n, 0, 100) * 2 - 100;
            }
        }
        return (numbers, names);
    }

    /// <summary>Formata o texto para o comando SPEAK do SSIP.</summary>
    internal static string EscapeText(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => l.StartsWith('.') ? "." + l : l); // "." no começo precisa ser dobrado
        return string.Join("\r\n", lines) + "\r\n.\r\n";
    }

    private void Connect()
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Connect(new UnixDomainSocketEndPoint(SocketPath()));
        socket = s;
        var q = new BlockingCollection<string[]>();
        replies = q;
        new Thread(() => Read(s, q)) { IsBackground = true }.Start();

        var user = Environment.GetEnvironmentVariable("USER") ?? "ark";
        var (numbers, names) = VoiceForLanguage();
        var cmds = new List<string> { $"SET SELF CLIENT_NAME {user}:a11y:{client}" };
        // módulo antes do idioma, e voz depois: trocar o idioma pode trocar a voz
        if (names.TryGetValue("VOICE_MODULE", out var module)) cmds.Add("SET SELF OUTPUT_MODULE " + module);
        cmds.Add("SET SELF LANGUAGE " + language);
        if (names.TryGetValue("VOICE", out var voice)) cmds.Add("SET SELF SYNTHESIS_VOICE " + voice);
        foreach (var key in new[] { "RATE", "PITCH", "VOLUME" })
            if (numbers.TryGetValue(key, out int v)) cmds.Add($"SET SELF {key} {v}");
        cmds.Add("SET SELF NOTIFICATION END on");
        cmds.Add("SET SELF NOTIFICATION CANCEL on");
        foreach (var c in cmds) Cmd(c);
        appliedVoice = VoiceFingerprint(numbers, names);
    }

    private static string VoiceFingerprint(Dictionary<string, int> numbers, Dictionary<string, string> names) =>
        string.Join(";", numbers.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + "|" +
        string.Join(";", names.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));

    private (Dictionary<string, int> Numbers, Dictionary<string, string> Names) VoiceForLanguage()
    {
        var values = UserVoice();
        string voiceBase = language switch { "pt-BR" => "pt-br", "en-US" => "en-us", "es-ES" => "es", "fr-FR" => "fr", _ => language.ToLowerInvariant() };
        if (values.Names.GetValueOrDefault("VOICE_MODULE") == "rhvoice" && !language.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            values.Names["VOICE_MODULE"] = "espeak-ng";
            values.Names["VOICE"] = voiceBase;
        }
        else if (values.Names.GetValueOrDefault("VOICE_MODULE") == "espeak-ng")
        {
            string configured = values.Names.GetValueOrDefault("VOICE", "");
            int plus = configured.IndexOf('+');
            values.Names["VOICE"] = voiceBase + (plus >= 0 ? configured[plus..] : "");
        }
        return values;
    }

    private void RefreshVoice()
    {
        var (numbers, names) = VoiceForLanguage();
        string fingerprint = VoiceFingerprint(numbers, names);
        if (fingerprint == appliedVoice) return;
        if (names.TryGetValue("VOICE_MODULE", out var module)) Cmd("SET SELF OUTPUT_MODULE " + module);
        Cmd("SET SELF LANGUAGE " + language);
        if (names.TryGetValue("VOICE", out var voice)) Cmd("SET SELF SYNTHESIS_VOICE " + voice);
        foreach (var key in new[] { "RATE", "PITCH", "VOLUME" })
            if (numbers.TryGetValue(key, out int value)) Cmd($"SET SELF {key} {value}");
        Cmd("SET SELF SSML_MODE off");
        appliedVoice = fingerprint;
    }

    /// <summary>Separa respostas de comandos de eventos 7xx (fim ou corte de fala).</summary>
    private void Read(Socket s, BlockingCollection<string[]> q)
    {
        try
        {
            using var reader = new StreamReader(new NetworkStream(s), Encoding.UTF8);
            var block = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                block.Add(line);
                if (line.Length < 4 || line[3] != ' ') continue; // "NNN-..." continua
                if (line[0] == '7') Event(block);
                else q.Add(block.ToArray());
                block.Clear();
            }
        }
        catch (Exception) { }
        finally
        {
            q.CompleteAdding();
        }
    }

    private void Event(List<string> block)
    {
        if (block.Count < 3) return;
        string id = block[0][4..];
        bool finished = block[^1].StartsWith("702"); // 702 END, 703 CANCEL
        Action<bool>? fn;
        lock (sync)
        {
            if (callbacks.Remove(id, out fn)) { }
            else
            {
                if (early.Count > 256) early.Clear();
                early[id] = finished;
            }
        }
        if (fn != null) post(() => fn(finished));
    }

    private string[] Cmd(string command)
    {
        socket!.Send(Encoding.UTF8.GetBytes(command + "\r\n"));
        return Reply();
    }

    private string[] Reply()
    {
        if (replies!.TryTake(out var r, TimeSpan.FromSeconds(5))) return r;
        throw new IOException("speech-dispatcher não respondeu");
    }

    private bool Ensure()
    {
        if (socket == null && available)
        {
            try { Connect(); }
            catch (Exception)
            {
                Close();
            }
        }
        return socket != null;
    }

    internal void Close()
    {
        try { socket?.Dispose(); } catch (Exception) { }
        socket = null;
    }

    /// <summary>
    /// Fala o texto. Com queue=false, interrompe a fala anterior. onDone recebe
    /// true quando a fala termina e false se ela foi cortada.
    /// </summary>
    public void Say(string text, bool queue = false, Action<bool>? onDone = null)
    {
        SpeechHistory.Record(text);
        if (SpeechOff())
        {
            if (onDone != null) post(() => onDone(true));
            return;
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!Ensure())
            {
                Console.Error.WriteLine("[fala] " + text);
                if (onDone != null) post(() => onDone(true));
                return;
            }
            try
            {
                Speak(text, queue, onDone);
                return;
            }
            catch (Exception)
            {
                Close();
            }
        }
    }

    internal static bool SpeechOff() => File.Exists(
        Environment.GetEnvironmentVariable("A11Y_FALA_DESLIGADA") ?? "/run/a11y-fala/fala-desligada");

    private void Speak(string text, bool queue, Action<bool>? onDone)
    {
        RefreshVoice();
        if (!queue) Cmd("STOP SELF");
        Cmd("SPEAK");
        socket!.Send(Encoding.UTF8.GetBytes(EscapeText(text)));
        var r = Reply();
        if (onDone == null || r.Length < 2) return;
        string id = r[0][4..]; // 225-<id da mensagem> / 225 OK MESSAGE QUEUED
        bool ended, finished;
        lock (sync)
        {
            ended = early.Remove(id, out finished);
            if (!ended) callbacks[id] = onDone;
        }
        if (ended) post(() => onDone(finished));
    }

    /// <summary>Cala a fala atual.</summary>
    public void Hush()
    {
        if (!Ensure()) return;
        try { Cmd("STOP SELF"); }
        catch (Exception) { Close(); }
    }
}
