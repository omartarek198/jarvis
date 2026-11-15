// Smoke tests for the voice pipeline: SpeechTextFormatter rules, chunking, mastering DSP.
// Run: dotnet run --project tools/voice-tests   (exit code 0 = all green)
using Jarvis.Kernel.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var failures = 0;

void Eq(string name, string actual, string expected)
{
    if (actual == expected) { Console.WriteLine($"  ok  {name}"); return; }
    failures++;
    Console.WriteLine($"FAIL  {name}\n      expected: {expected}\n      actual  : {actual}");
}

void Contains(string name, string actual, string fragment)
{
    if (actual.Contains(fragment)) { Console.WriteLine($"  ok  {name}"); return; }
    failures++;
    Console.WriteLine($"FAIL  {name}\n      missing : {fragment}\n      actual  : {actual}");
}

void NotContains(string name, string actual, string fragment)
{
    if (!actual.Contains(fragment)) { Console.WriteLine($"  ok  {name}"); return; }
    failures++;
    Console.WriteLine($"FAIL  {name}\n      must not contain: {fragment}\n      actual  : {actual}");
}

string F(string s, int max = 420) => SpeechTextFormatter.Format(s, max);

Console.WriteLine("== SpeechTextFormatter ==");
Eq("acronyms", F("The UI calls the API."), "The U I calls the A P I.");
Eq("ticket", F("Look at ABC-12 today."), "Look at A B C twelve today.");
Eq("localhost", F("Kernel on localhost:5588 now."), "Kernel on local host, port fifty five eighty eight now.");
Eq("duration", F("Build took 0h-07m-12s total."), "Build took seven minutes, twelve seconds total.");
Eq("duration h", F("Ran 1h-02m-05s."), "Ran one hour, two minutes, five seconds.");
Eq("percent", F("Coverage is 82% now."), "Coverage is 82 percent now.");
Eq("snake", F("Use switch_voice for that."), "Use switch voice for that.");
Contains("url stays link", F("See https://example.com/x?y=1 please."), "link");
NotContains("url not port-mangled", F("See http://localhost:5588/api please."), "port");
Contains("guid", F("Run 3F2504E0-4F89-11D3-9A0C-0305E82C3301 failed."), "an id");
Contains("stacktrace gone", F("Boom\n   at Jarvis.Kernel.Foo.Bar(String x)\n   at Program.Main()"), "Boom");
NotContains("stacktrace frames gone", F("Boom\n   at Jarvis.Kernel.Foo.Bar(String x)"), " at Jarvis");
Eq("exception name", F("It threw NullReferenceException: boom."), "It threw an error: boom.");
Eq("small numbers", F("3 issues remain."), "three issues remain.");
Eq("arrows", F("main -> release"), "main to release");
Contains("cap points at console", F(string.Join(" ", Enumerable.Repeat("This is a long sentence about work.", 30)), 420),
    "There's more, it's in the console.");
Eq("lexicon override", SpeechTextFormatter.Format("Ship WidgetCo now.", 420,
    new Dictionary<string, string> { ["WidgetCo"] = "Widget Company" }), "Ship Widget Company now.");
Eq("paragraphs kept", F("Done.\n\nNext: tests."), "Done.\nNext: tests.");
Eq("empty", F("   "), "");

Console.WriteLine("== SpeechChunker ==");
var one = SpeechChunker.Split("Done.", new VoiceChunking());
Eq("short stays single", one.Count.ToString(), "1");
var text = "First sentence here. Second sentence follows. " +
           "Third one is a bit longer than the others to force a boundary. Fourth closes it out.\n" +
           "New paragraph starts. And ends.";
var chunks = SpeechChunker.Split(text, new VoiceChunking { MaxChunkChars = 60, MinSplitChars = 40 });
Eq("order preserved", string.Join(" ", chunks.Select(c => c.Text)).Replace("  ", " "),
    text.Replace("\n", " "));
if (chunks.Count < 3) { failures++; Console.WriteLine($"FAIL  chunk count ({chunks.Count})"); }
else Console.WriteLine($"  ok  chunked into {chunks.Count}");
if (chunks[^1].GapAfterMs != 0) { failures++; Console.WriteLine("FAIL  last gap must be 0"); }
else Console.WriteLine("  ok  last gap 0");
var paraChunk = chunks.Take(chunks.Count - 1).Any(c => c.GapAfterMs > new VoiceChunking().SentenceGapMs);
Console.WriteLine(paraChunk ? "  ok  paragraph gap longer" : "FAIL  paragraph gap longer");
if (!paraChunk) failures++;

Console.WriteLine("== AudioMastering ==");
// 0.5s of quiet 200Hz tone + a loud click: mastering must raise loudness, kill DC/rumble, not clip
var rate = 24000;
var pcm = new byte[rate]; // 0.5s 16-bit mono
for (var i = 0; i < pcm.Length / 2; i++)
{
    var t = i / (double)rate;
    var s = (short)(2500 * Math.Sin(2 * Math.PI * 200 * t) + 1500); // quiet tone + DC offset
    if (i == 6000) s = 6000;                                         // one modest transient
    BitConverter.GetBytes(s).CopyTo(pcm, i * 2);
}
double Rms(byte[] p)
{
    double sum = 0; var n = p.Length / 2;
    for (var i = 0; i < n; i++) { double v = BitConverter.ToInt16(p, i * 2) / 32768.0; sum += v * v; }
    return Math.Sqrt(sum / n);
}
var mastered = AudioMastering.Master(pcm, rate);
Eq("length preserved", mastered.Length.ToString(), pcm.Length.ToString());
var before = Rms(pcm); var after = Rms(mastered);
Console.WriteLine($"      rms {before:F4} -> {after:F4}");
if (after <= before) { failures++; Console.WriteLine("FAIL  loudness should rise for quiet input"); }
else Console.WriteLine("  ok  loudness normalized up");
short peak = 0;
for (var i = 0; i < mastered.Length / 2; i++)
{
    var v = Math.Abs((int)BitConverter.ToInt16(mastered, i * 2));
    if (v > peak) peak = (short)v;
}
Console.WriteLine(peak <= 31000 ? $"  ok  peak under ceiling ({peak})" : $"FAIL  peak too hot ({peak})");
if (peak > 31000) failures++;
// DC offset must be gone (high-pass)
double mean = 0; for (var i = 0; i < mastered.Length / 2; i++) mean += BitConverter.ToInt16(mastered, i * 2);
mean /= mastered.Length / 2.0;
Console.WriteLine(Math.Abs(mean) < 150 ? $"  ok  DC removed ({mean:F1})" : $"FAIL  DC remains ({mean:F1})");
if (Math.Abs(mean) >= 150) failures++;

Console.WriteLine("== TtsService end-to-end (fallback chain, no kernel) ==");
// Real TtsService with stub DI: kokoro may or may not be up on this machine, piper is
// typically absent in a worktree — the chain must ALWAYS produce audio (SAPI floor) and
// must never cache audio rendered by a fallback engine.
{
    using var loggerFactory = LoggerFactory.Create(_ => { });
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var events = new List<Jarvis.Core.JarvisEvent>();
    var tts = new TtsService(scopes, loggerFactory.CreateLogger<TtsService>(), new FakeBus(events));

    // start from a cold cache — a previous run's hit would mask the render path
    try { Directory.Delete(Path.Combine(Jarvis.Core.JarvisPaths.Voice, "cache"), recursive: true); } catch { }

    var wav = await tts.SynthesizeAsync("Confirmed.", "default");
    var first = tts.LastEngineUsed;
    Console.WriteLine($"      engine: {first}, {wav.Length} bytes");
    if (wav.Length <= 44) { failures++; Console.WriteLine("FAIL  no audio from fallback chain"); }
    else Console.WriteLine("  ok  audio produced");
    if (first is not ("kokoro" or "piper" or "sapi")) { failures++; Console.WriteLine($"FAIL  unexpected engine {first}"); }
    else Console.WriteLine("  ok  engine valid");

    var wav2 = await tts.SynthesizeAsync("Confirmed.", "default");
    var second = tts.LastEngineUsed;
    if (first == "kokoro" && second != "cache") { failures++; Console.WriteLine($"FAIL  primary-engine phrase not cached ({second})"); }
    else if (first != "kokoro" && second == "cache") { failures++; Console.WriteLine("FAIL  fallback audio was cached"); }
    else Console.WriteLine($"  ok  cache policy ({first} -> {second})");
    if (wav2.Length <= 44) { failures++; Console.WriteLine("FAIL  second synth empty"); }

    // boris prefers piper; with piper absent it must land on SAPI, not crash
    var wav3 = await tts.SynthesizeAsync("Privet, comrade.", "boris");
    Console.WriteLine(wav3.Length > 44 && tts.LastEngineUsed is "piper" or "sapi"
        ? $"  ok  boris fell to {tts.LastEngineUsed}"
        : $"FAIL  boris path ({tts.LastEngineUsed}, {wav3.Length} bytes)");
    if (!(wav3.Length > 44 && tts.LastEngineUsed is "piper" or "sapi")) failures++;

    if (events.Count(e => e.Kind == "voice.tts") != 3) { failures++; Console.WriteLine($"FAIL  expected 3 voice.tts events, got {events.Count}"); }
    else Console.WriteLine("  ok  voice.tts events published");
}

Console.WriteLine(failures == 0 ? "\nALL GREEN" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

sealed class FakeBus(List<Jarvis.Core.JarvisEvent> sink) : Jarvis.Core.IEventBus
{
    public void Publish(Jarvis.Core.JarvisEvent e) => sink.Add(e);
    public IDisposable Subscribe(Func<Jarvis.Core.JarvisEvent, Task> handler) => new Dummy();
    private sealed class Dummy : IDisposable { public void Dispose() { } }
}
