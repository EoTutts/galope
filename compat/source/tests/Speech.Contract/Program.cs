using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using R36S.A11y;

string temp = Path.Combine(Path.GetTempPath(), "a11y-"+Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(temp);
string config = Path.Combine(temp, "config.json"), legacy = Path.Combine(temp, "voz.conf"),
    history = Path.Combine(temp, "history.txt"), muted = Path.Combine(temp, "muted"), address = Path.Combine(temp, "s.sock");
string[] keys = ["A11Y_VOZ_CONF", "A11Y_CONFIG", "A11Y_HISTORICO_FALAS", "A11Y_FALA_DESLIGADA", "SPEECHD_ADDRESS"];
var previous = keys.ToDictionary(key=>key, Environment.GetEnvironmentVariable);
int checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
void WriteVoice(int rate, string name) => File.WriteAllText(config, JsonSerializer.Serialize(new {
    system = new { RATE=1, VOICE="Não usar esta voz" },
    voice = new { RATE=rate, PITCH=50, VOLUME="75", VOICE_MODULE="rhvoice", VOICE=name }
}));
try {
    Environment.SetEnvironmentVariable("A11Y_CONFIG", config);
    Environment.SetEnvironmentVariable("A11Y_VOZ_CONF", null);
    Environment.SetEnvironmentVariable("A11Y_HISTORICO_FALAS", history);
    Environment.SetEnvironmentVariable("A11Y_FALA_DESLIGADA", muted);
    Environment.SetEnvironmentVariable("SPEECHD_ADDRESS", "unix_socket:"+address);
    WriteVoice(78, "Letícia-F123");
    var voice = Speech.UserVoice();
    Check(voice.Numbers["RATE"] == 56 && voice.Numbers["VOLUME"] == 50, "compact JSON/rate conversion");
    Check(voice.Names["VOICE"] == "Letícia-F123", "JSON escaped Unicode or wrong section");
    File.WriteAllText(legacy, "RATE = 12\nPITCH=200\nVOLUME=-1\nVOICE_MODULE='espeak-ng'\nVOICE='pt-br+f2'\n");
    Environment.SetEnvironmentVariable("A11Y_VOZ_CONF", legacy);
    voice = Speech.UserVoice();
    Check(voice.Numbers["RATE"] == -76 && voice.Numbers["PITCH"] == 100 && voice.Numbers["VOLUME"] == -100, "legacy override/clamping");
    Environment.SetEnvironmentVariable("A11Y_VOZ_CONF", null);
    File.WriteAllText(config, "{ broken");
    Check(Speech.UserVoice().Numbers.Count == 0, "invalid JSON must not invent a preference");
    WriteVoice(78, "Letícia-F123");

    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(address)); listener.Listen(1);
    var commands = new ConcurrentQueue<string>();
    var messages = new ConcurrentQueue<string>();
    var server = Task.Run(() => {
        using var connection = listener.Accept();
        using var stream = new NetworkStream(connection);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine="\r\n", AutoFlush=true };
        int id=0;
        try { while (reader.ReadLine() is {} command) {
            commands.Enqueue(command);
            if (command != "SPEAK") { writer.WriteLine("200 OK"); continue; }
            writer.WriteLine("230 OK RECEIVING DATA");
            var lines = new List<string>();
            while (reader.ReadLine() is {} line && line != ".") lines.Add(line);
            messages.Enqueue(string.Join("\n", lines)); id++;
            writer.Write($"225-{id}\r\n225 OK MESSAGE QUEUED\r\n702-{id}\r\n702-1\r\n702 END\r\n");
            writer.Flush();
        } } catch (IOException) { /* Socket disposal may reset an AF_UNIX peer on Windows. */ }
    });
    var speech = new Speech("contract-test", "pt-BR", fn=>fn());
    try {
        int ended=0;
        speech.Say("Primeira fala", onDone:ok=>{if(ok) Interlocked.Increment(ref ended);});
        Check(SpinWait.SpinUntil(()=>ended==1, 2000), "SSIP END callback");
        Check(commands.Contains("SET SELF SYNTHESIS_VOICE Letícia-F123") && commands.Contains("SET SELF RATE 56"), "SSIP must use Luma voice");
        WriteVoice(20, "Voz nova");
        speech.Say("Segunda fala", queue:true);
        Check(commands.Contains("SET SELF SYNTHESIS_VOICE Voz nova") && commands.Contains("SET SELF RATE -60"), "Fn configuration refresh without reopening game");
        Check(commands.Count(c=>c=="STOP SELF")==1, "queue/cancel behavior must remain client-local");
        File.WriteAllText(muted, "1");
        int transmitted = messages.Count;
        bool completed=false;
        speech.Say("Fala silenciada", onDone:ok=>completed=ok);
        Check(completed && messages.Count==transmitted, "Fn+Y must mute without blocking callbacks");
        Check(File.ReadAllLines(history).Length==3, "history must include muted speech");
    } finally { speech.Close(); }
    Check(server.Wait(2000), "speech socket/thread cleanup");
    Console.WriteLine($"Speech contract: {checks} checks passed (JSON, legacy, SSIP, live settings, mute, history, callbacks).");
} finally {
    foreach (var (key,value) in previous) Environment.SetEnvironmentVariable(key,value);
    Directory.Delete(temp, true);
}
