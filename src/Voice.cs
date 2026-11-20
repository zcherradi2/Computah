using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Assist;

/// Speech to text, entirely on this machine: whisper.cpp (Whisper.net, bundled; CPU, no install, no GPU needed) with a
/// small model downloaded once on first use into %LOCALAPPDATA%\Computah\models. Works the same for every agent: the
/// agent only ever receives the transcript.
static class Voice
{
    public sealed record Model(string Id, string Label, string File, long Bytes);

    /// "base" is fast on any CPU (about 1/3 of real time on a mid-range desktop); "small" is more accurate but ~3x slower.
    public static readonly Model[] Models =
    {
        new("base", "Base: fast (60 MB)", "ggml-base-q5_1.bin", 59_707_625),
        new("small", "Small: more accurate, slower (190 MB)", "ggml-small-q5_1.bin", 190_085_487),
    };

    public static Model ModelFor(Settings s) => Models.FirstOrDefault(m => m.Id == s.VoiceModel) ?? Models[0];
    public static string ModelsDir => Path.Combine(Db.DataDir, "models");
    public static string PathOf(Model m) => Path.Combine(ModelsDir, m.File);
    public static bool IsReady(Model m) => File.Exists(PathOf(m)) && new FileInfo(PathOf(m)).Length == m.Bytes;

    static readonly SemaphoreSlim downloadGate = new(1, 1), runGate = new(1, 1);
    static WhisperFactory? factory;
    static string? factoryPath;

    /// Downloads the model if it isn't there yet (to a .part file, renamed when complete). progress: 0..1.
    public static async Task EnsureModelAsync(Model m, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsReady(m)) return;
        await downloadGate.WaitAsync(ct);
        try
        {
            if (IsReady(m)) return;
            Directory.CreateDirectory(ModelsDir);
            var part = PathOf(m) + ".part";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{m.File}",
                                                 HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report(Math.Min(1, (double)done / m.Bytes));
                }
            }
            if (new FileInfo(part).Length != m.Bytes) { File.Delete(part); throw new IOException("the download was incomplete"); }
            File.Move(part, PathOf(m), overwrite: true);
        }
        finally { downloadGate.Release(); }
    }

    /// 16 kHz mono samples → text. prompt: words likely to be said (window title, button names): whisper spells them
    /// correctly when they are in its prompt ("Ajouter gamme", not "a jitter gammie").
    public static async Task<string> TranscribeAsync(Model m, float[] samples, string? prompt, string language, CancellationToken ct)
    {
        if (samples.Length < 16000 / 4) return ""; // under a quarter second: nothing said
        float peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v));
        if (peak < 0.02f) return "";                // silence (mic muted, nothing said): skip the model
        await runGate.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                if (factory == null || factoryPath != PathOf(m))
                {
                    factory?.Dispose();
                    RuntimeOptions.RuntimeLibraryOrder = new() { RuntimeLibrary.Cpu };
                    factory = WhisperFactory.FromPath(PathOf(m), new WhisperFactoryOptions { UseGpu = false });
                    factoryPath = PathOf(m);
                }
                var b = factory.CreateBuilder()
                               .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                               .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                               .WithNoSpeechThreshold(0.6f);
                if (!string.IsNullOrWhiteSpace(prompt)) b = b.WithPrompt(prompt);
                await using var p = b.Build();
                var sb = new StringBuilder();
                await foreach (var seg in p.ProcessAsync(samples, ct))
                    if (seg.NoSpeechProbability < 0.6f) sb.Append(seg.Text);
                return Clean(sb.ToString());
            }, ct);
        }
        finally { runGate.Release(); }
    }

    /// Whisper marks silence and noises as "[BLANK_AUDIO]", "(music)" and the like.
    static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\((?:music|applause|silence|noise|laughter|inaudible)[^)]*\)", "",
                                                         System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// Words on screen that the user may say: the window title and the element names, most useful first, kept short
    /// (whisper only reads the end of a long prompt).
    public static string? PromptFrom(CapturedContext? ctx, UiMap? elements)
    {
        var words = new List<string>();
        if (ctx != null && ctx.WindowTitle.Length > 0) words.Add(ctx.WindowTitle);
        if (elements != null)
            words.AddRange(elements.Items.Where(e => e.Type != "text").Select(e => e.Name)
                                   .Concat(elements.Items.Where(e => e.Type == "text").Select(e => e.Name)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var w in words.Select(w => w.Trim()).Where(w => w.Length is > 1 and < 40))
        {
            if (!seen.Add(w)) continue;
            if (sb.Length + w.Length > 500) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(w);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}

/// The default microphone, 16 kHz mono, kept in memory until Stop(). Level: 0..1, for the "listening" display.
public sealed class Recorder : IDisposable
{
    const int Rate = 16000, MaxSeconds = 120;
    readonly WaveIn wave = new() { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50 };
    readonly List<float> samples = new();
    readonly object gate = new();
    bool stopped;

    public event Action<float>? Level;
    public TimeSpan Length { get { lock (gate) return TimeSpan.FromSeconds(samples.Count / (double)Rate); } }

    public static bool HasMicrophone => WaveIn.DeviceCount > 0;

    public Recorder()
    {
        wave.DataAvailable += (_, e) =>
        {
            float peak = 0;
            lock (gate)
            {
                if (stopped || samples.Count > Rate * MaxSeconds) return;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    float v = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    samples.Add(v);
                    peak = Math.Max(peak, Math.Abs(v));
                }
            }
            Level?.Invoke(Math.Min(1, peak * 3));
        };
    }

    public void Start() => wave.StartRecording();

    public float[] Stop()
    {
        lock (gate) stopped = true;
        try { wave.StopRecording(); } catch { }
        lock (gate) return samples.ToArray();
    }

    public void Dispose() { try { wave.StopRecording(); } catch { } wave.Dispose(); }
}
