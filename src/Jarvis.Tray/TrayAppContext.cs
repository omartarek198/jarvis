using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jarvis.Tray;

public sealed class TrayAppContext : ApplicationContext
{
    private static readonly string KernelUrl =
        Environment.GetEnvironmentVariable("JARVIS_URL") ?? "http://localhost:5588";

    // Event kinds whose payload.spoken should be announced out loud, unprompted
    private static readonly HashSet<string> SpokenKinds =
        ["agent.done", "briefing.ready", "focus.ended", "playwright.done", "reminder.due",
         "teams.message", "teams.mention", "email.vip", "graph.code", "graph.connected", "habit.nag", "focus.nag",
         "away.exited"];

    private readonly HttpClient _http = new() { BaseAddress = new Uri(KernelUrl), Timeout = TimeSpan.FromSeconds(120) };
    private readonly NotifyIcon _icon;
    private readonly KeyboardHook _hook;
    private readonly Recorder _recorder = new();
    private HubConnection? _hub;

    private readonly Lock _playLock = new();
    private IWavePlayer? _player;
    private WaveFileReader? _playReader;
    private CancellationTokenSource? _graceCts;
    private string _outputMode = "auto"; // auto | speakers | headset (refreshed from converse headers)

    /* wake word + conversation mode state */
    private readonly WakeListener _wake = new();
    private MeetingScribe? _scribe;
    private volatile bool _wakeEnabled = true;
    private volatile bool _convMode;
    private volatile bool _isPlaying;
    private volatile bool _lastPlayWasReply;
    private long _lastPlaybackEndTicks;
    private long _lastWakeTicks;
    private int _followUpBusy;   // interlocked guard: one VAD capture at a time

    public TrayAppContext()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "JARVIS — hold Right-Ctrl to talk",
            ContextMenuStrip = BuildMenu(),
        };

        var vk = int.TryParse(Environment.GetEnvironmentVariable("JARVIS_PTT_VK"), out var v) ? v : 0xA3;
        _hook = new KeyboardHook(vk);
        _hook.PttDown += OnPttDown;
        _hook.PttUp += OnPttUp;

        _icon.ShowBalloonTip(2500, "JARVIS online",
            $"Hold Right-Ctrl and speak — or just say \"Jarvis\".\nMic: {_recorder.Probe()}", ToolTipIcon.Info);

        _wake.Detected += OnWakeDetected;
        _wake.Start();

        // Voice telemetry: stream 3-channel levels to the console scope at 10Hz
        _ = Task.Run(async () =>
        {
            static float Norm(int peak) => (float)Math.Pow(Math.Min(1.0, peak / 11000.0), 0.6);
            while (true)
            {
                try
                {
                    var a = Norm(_wake.AmbientPeak);
                    var c = _recorder.IsCapturing ? Norm(_recorder.RecentPeak) : 0f;
                    var s = CurrentSpeechLevel();
                    await _http.PostAsJsonAsync("/api/voice/levels",
                        new { a = Math.Round(a, 3), c = Math.Round(c, 3), s = Math.Round(s, 3) });
                }
                catch { /* kernel down — keep trying */ }
                await Task.Delay(100);
            }
        });

        // Lock/unlock feeds the kernel's sleep inference
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        _ = ConnectEventFeedAsync();
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        var state = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLock => "locked",
            Microsoft.Win32.SessionSwitchReason.SessionUnlock => "unlocked",
            _ => null,
        };
        if (state is null) return;
        _ = _http.PostAsJsonAsync("/api/session/state", new { state })
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open console", null, (_, _) =>
            Process.Start(new ProcessStartInfo(KernelUrl) { UseShellExecute = true }));
        menu.Items.Add("Status", null, async (_, _) => await ShowStatusAsync());
        var wakeItem = new ToolStripMenuItem("Wake word (\"Jarvis\")") { Checked = true, CheckOnClick = true };
        wakeItem.CheckedChanged += (_, _) => SetWakeEnabled(wakeItem.Checked);
        menu.Items.Add(wakeItem);
        menu.Items.Add("Stop everything", null, async (_, _) =>
        {
            StopPlayback();
            _recorder.EndSession();
            try { await _http.PostAsync("/api/system/stop", null); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        return menu;
    }

    /* ---------------- push-to-talk ---------------- */

    private void OnPttDown()
    {
        CancelGrace();          // a new exchange started — keep the session alive
        StopPlayback();         // barge-in
        try
        {
            _recorder.StartCapture();   // opens the session if needed; BT enters call-mode once
            _icon.Text = "JARVIS — listening…";
            PostStateFireAndForget("listening");
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(3000, "Microphone error", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnPttUp()
    {
        var wav = _recorder.StopCapture();   // session stays open — no BT profile flip before the reply
        _icon.Text = "JARVIS — hold Right-Ctrl to talk";

        if (wav.Length < 16000) // <0.5s: accidental tap
        {
            PostStateFireAndForget("idle");
            ScheduleSessionEnd();
            return;
        }

        if (Recorder.Peak(wav) < 200)
        {
            PostStateFireAndForget("idle");
            _ = HandleDeadMicAsync();
            return;
        }

        PostStateFireAndForget("thinking");
        if (_dictation) _ = HandleDictationAsync(wav);
        else _ = HandleUtteranceAsync(wav);
    }

    /* ---------------- dictation: transcribe-only, type into the foreground window ---------------- */

    private volatile bool _dictation;

    private async Task HandleDictationAsync(byte[] wav)
    {
        try
        {
            using var content = new ByteArrayContent(wav);
            content.Headers.ContentType = new("audio/wav");
            var resp = await _http.PostAsync("/api/voice/transcribe", content);
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var text = doc.TryGetProperty("text", out var t) ? t.GetString() : null;
            PostStateFireAndForget("idle");
            if (string.IsNullOrWhiteSpace(text)) return;

            // The exit phrase must still work while everything else is typed
            if (System.Text.RegularExpressions.Regex.IsMatch(text.Trim().TrimEnd('.', '!'),
                    @"^(stop|end) dictation$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                _ = _http.PostAsJsonAsync("/api/command", new { text = "stop dictation", source = "voice" });
                return;
            }
            TextInjector.Type(text.Trim() + " ");
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(3000, "Dictation error", ex.Message, ToolTipIcon.Error);
        }
        finally { ScheduleSessionEnd(); }
    }

    /* ---------------- dead-mic auto-fallback ---------------- */

    private async Task HandleDeadMicAsync()
    {
        var deadDevice = _recorder.DeviceName;
        _recorder.EndSession(); // release the silent device before metering
        var meters = await Task.Run(() => Recorder.MeterAllDevices());
        var live = meters.FirstOrDefault(m => m.Peak > 300 &&
            !m.Name.Equals(deadDevice, StringComparison.OrdinalIgnoreCase));

        string message;
        if (live.Name is not null)
        {
            _recorder.OverrideDevice(live.Name);
            message = $"Your {Short(deadDevice)} delivered silence — I've switched to {Short(live.Name)}. Try again.";
        }
        else
        {
            message = $"Your microphone {Short(deadDevice)} delivered silence and no other live mic was found. Check the headset.";
        }
        _icon.ShowBalloonTip(5000, "Microphone", message, ToolTipIcon.Warning);
        await SpeakAsync(message);
    }

    private static string Short(string device) => device.Length > 28 ? device[..28] : device;

    /* ---------------- thinking tune: audible "I heard you, processing" ---------------- */

    private static readonly byte[] ThinkingBlip = GenerateThinkingBlip();
    private CancellationTokenSource? _thinkCts;

    /// <summary>Soft rising two-note blip (E5→A5), quiet, ~260ms — synthesized once.</summary>
    private static byte[] GenerateThinkingBlip()
    {
        const int rate = 16000;
        double[] notes = [659.25, 880.0];
        int[] lens = [90, 130];
        var samples = new List<short>();
        for (var n = 0; n < notes.Length; n++)
        {
            var count = rate * lens[n] / 1000;
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Sin(Math.PI * i / count);   // fade in/out: no clicks
                var v = Math.Sin(2 * Math.PI * notes[n] * i / rate) * envelope * 0.16;
                samples.Add((short)(v * short.MaxValue));
            }
            for (var i = 0; i < rate * 40 / 1000; i++) samples.Add(0);
        }
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(ms), new WaveFormat(rate, 16, 1)))
        {
            var bytes = new byte[samples.Count * 2];
            Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
            w.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private void PlayBlipOnce()
    {
        try
        {
            var reader = new WaveFileReader(new MemoryStream(ThinkingBlip));
            var device = PickRenderDevice(_outputMode);
            var player = device is null
                ? new WasapiOut(AudioClientShareMode.Shared, 120)
                : new WasapiOut(device, AudioClientShareMode.Shared, true, 120);
            player.PlaybackStopped += (_, _) => { try { player.Dispose(); reader.Dispose(); } catch { } };
            player.Init(reader);
            player.Play();
        }
        catch { /* the tune is cosmetic — never let it break the round trip */ }
    }

    /// <summary>Blip immediately ("heard you"), then every 2.5s while still waiting.</summary>
    private void StartThinkingTune()
    {
        StopThinkingTune();
        var cts = new CancellationTokenSource();
        _thinkCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                PlayBlipOnce();
                try { await Task.Delay(2500, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    private void StopThinkingTune()
    {
        try { _thinkCts?.Cancel(); } catch { }
        _thinkCts = null;
    }

    /* ---------------- voice round trip ---------------- */

    private async Task HandleUtteranceAsync(byte[] wav, bool wakeTriggered = false)
    {
        try
        {
            _icon.Text = "JARVIS — thinking…";
            StartThinkingTune();
            using var content = new ByteArrayContent(wav);
            content.Headers.ContentType = new("audio/wav");
            if (wakeTriggered) content.Headers.Add("X-Jarvis-Trigger", "wake");
            var resp = await _http.PostAsync("/api/voice/converse", content);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) { ScheduleSessionEnd(); return; }

            var transcript = HeaderText(resp, "X-Jarvis-Transcript");
            var spoken = HeaderText(resp, "X-Jarvis-Spoken");
            var output = HeaderText(resp, "X-Jarvis-Output");
            if (output is "auto" or "speakers" or "headset") _outputMode = output;
            _convMode = HeaderText(resp, "X-Jarvis-Conv") == "on";
            _listenAfterReply = HeaderText(resp, "X-Jarvis-Listen") == "1";   // "Jarvis…" → "Yes?" → keep listening
            var wakeSetting = HeaderText(resp, "X-Jarvis-Wake");
            if (wakeSetting is "on" or "off") SetWakeEnabled(wakeSetting == "on");
            var audio = await resp.Content.ReadAsByteArrayAsync();
            StopThinkingTune();

            if (!string.IsNullOrWhiteSpace(spoken))
                _icon.ShowBalloonTip(4000, string.IsNullOrWhiteSpace(transcript) ? "JARVIS" : transcript,
                    spoken.Length > 200 ? spoken[..200] + "…" : spoken, ToolTipIcon.None);

            if (audio.Length > 44) PlayWav(audio, isReply: true);
            else ScheduleSessionEnd();
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(3000, "JARVIS unreachable", ex.Message, ToolTipIcon.Error);
            ScheduleSessionEnd();
        }
        finally
        {
            StopThinkingTune();
            _icon.Text = "JARVIS — hold Right-Ctrl to talk";
        }
    }

    /* ---------------- playback (WASAPI, device-routable) ---------------- */

    /* speech envelope: peak per 50ms slice of the playing wav — feeds the VOICE trace */
    private float[] _speechEnv = [];
    private long _speechStartTicks;

    private float CurrentSpeechLevel()
    {
        if (!_isPlaying || _speechEnv.Length == 0) return 0;
        var idx = (int)((Environment.TickCount64 - Interlocked.Read(ref _speechStartTicks)) / 50);
        return idx >= 0 && idx < _speechEnv.Length ? _speechEnv[idx] : 0;
    }

    private static float[] BuildEnvelope(byte[] wav)
    {
        try
        {
            var rate = BitConverter.ToInt32(wav, 24);
            var sliceBytes = Math.Max(2, rate / 20 * 2);   // 50ms of 16-bit mono
            var env = new float[Math.Max(1, (wav.Length - 44) / sliceBytes + 1)];
            for (var i = 44; i + 1 < wav.Length; i += 2)
            {
                var s = Math.Abs((int)BitConverter.ToInt16(wav, i));
                var slot = (i - 44) / sliceBytes;
                if (slot < env.Length && s > env[slot] * 32767f) env[slot] = s / 32767f;
            }
            return env;
        }
        catch { return []; }
    }

    private void PlayWav(byte[] wav, bool isReply = false)
    {
        lock (_playLock)
        {
            StopPlaybackLocked();
            try
            {
                _speechEnv = BuildEnvelope(wav);
                Interlocked.Exchange(ref _speechStartTicks, Environment.TickCount64);
                _playReader = new WaveFileReader(new MemoryStream(wav));
                var device = PickRenderDevice(_outputMode);
                _player = device is null
                    ? new WasapiOut(AudioClientShareMode.Shared, 120)
                    : new WasapiOut(device, AudioClientShareMode.Shared, true, 120);
                _isPlaying = true;
                _lastPlayWasReply = isReply;
                _player.PlaybackStopped += (_, _) => OnPlaybackStopped();
                _player.Init(_playReader);
                _player.Play();
            }
            catch (Exception ex)
            {
                _icon.ShowBalloonTip(3000, "Playback error", ex.Message, ToolTipIcon.Error);
                _isPlaying = false;
                ScheduleSessionEnd();
            }
        }
    }

    private volatile bool _listenAfterReply;

    private void OnPlaybackStopped()
    {
        _isPlaying = false;
        Interlocked.Exchange(ref _lastPlaybackEndTicks, Environment.TickCount64);
        // Keep the mic open after MY reply (not announcements/barge-ins) when conversation
        // mode is on, or when the kernel asked for it ("Jarvis…" → "Yes?").
        var listen = _listenAfterReply; _listenAfterReply = false;
        if ((listen || _convMode) && _lastPlayWasReply && !_recorder.IsCapturing)
            _ = ConversationFollowUpAsync();
        else
            ScheduleSessionEnd();
    }

    private async Task ConversationFollowUpAsync()
    {
        if (Interlocked.CompareExchange(ref _followUpBusy, 1, 0) != 0) return;
        try
        {
            await Task.Delay(500);   // let the speaker tail fade — don't hear ourselves
            if (_recorder.IsCapturing) return;   // PTT took over
            var wav = await CaptureWithVadAsync(maxMs: 6000);
            if (wav.Length > 16000 && Recorder.Peak(wav) > 250)
            {
                PostStateFireAndForget("thinking");
                if (_dictation) await HandleDictationAsync(wav);
                else await HandleUtteranceAsync(wav);
            }
            else
            {
                PostStateFireAndForget("idle");
                ScheduleSessionEnd();
            }
        }
        finally { Interlocked.Exchange(ref _followUpBusy, 0); }
    }

    /// <summary>Open mic until 1.2s of post-speech silence, 3.5s of nothing, or maxMs.</summary>
    private async Task<byte[]> CaptureWithVadAsync(int maxMs)
    {
        try { _recorder.StartCapture(); } catch { return []; }
        PostStateFireAndForget("listening");
        var started = Environment.TickCount64;
        long speechAt = 0, lastLoudAt = 0;
        while (Environment.TickCount64 - started < maxMs)
        {
            await Task.Delay(150);
            var p = _recorder.RecentPeak;
            if (p > 600)
            {
                if (speechAt == 0) speechAt = Environment.TickCount64;
                lastLoudAt = Environment.TickCount64;
            }
            // 2s pause tolerance: people naturally stop after the wake word before asking
            if (speechAt > 0 && Environment.TickCount64 - lastLoudAt > 2000) break;
            if (speechAt == 0 && Environment.TickCount64 - started > 5000) break;
        }
        return _recorder.StopCapture();
    }

    /* ---------------- wake word ---------------- */

    private void SetWakeEnabled(bool on)
    {
        if (_wakeEnabled == on) return;
        _wakeEnabled = on;
        if (on) _wake.Start(); else _wake.Stop();
    }

    private void OnWakeDetected(double score)
    {
        var now = Environment.TickCount64;
        if (!_wakeEnabled) return;
        if (_recorder.IsCapturing) return;                                                    // PTT in progress
        if (now - Interlocked.Read(ref _lastWakeTicks) < 3000) return;                        // cooldown
        if (_isPlaying) { StopThinkingTune(); StopPlayback(); }                               // "Jarvis" = shut up and listen
        else if (now - Interlocked.Read(ref _lastPlaybackEndTicks) < 300) return;             // tiny anti-echo window
        Interlocked.Exchange(ref _lastWakeTicks, now);

        _ = Task.Run(async () =>
        {
            if (Interlocked.CompareExchange(ref _followUpBusy, 1, 0) != 0) return;
            try
            {
                System.Media.SystemSounds.Asterisk.Play();   // chime-then-listen: never act on the wake word alone
                var wav = await CaptureWithVadAsync(maxMs: 7000);
                if (wav.Length > 16000 && Recorder.Peak(wav) > 250)
                {
                    PostStateFireAndForget("thinking");
                    if (_dictation) await HandleDictationAsync(wav);
                    else await HandleUtteranceAsync(wav, wakeTriggered: true);
                }
                else
                {
                    // Called "Jarvis" but said nothing more — acknowledge and keep listening
                    PostStateFireAndForget("idle");
                    _listenAfterReply = true;
                    await SpeakAsync("Yes?", isReply: true);
                }
            }
            finally { Interlocked.Exchange(ref _followUpBusy, 0); }
        });
    }

    /// <summary>speakers = first active render endpoint that isn't the comms headset; headset = comms default; auto = system default.</summary>
    private static MMDevice? PickRenderDevice(string mode)
    {
        if (mode == "auto") return null;
        try
        {
            var mm = new MMDeviceEnumerator();
            if (mode == "headset")
                return mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

            // speakers: avoid the same hardware as the comms capture device (the headset model name in parens)
            var headsetToken = "";
            try
            {
                var comm = mm.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName;
                var open = comm.IndexOf('('); var close = comm.IndexOf(')');
                if (open >= 0 && close > open) headsetToken = comm[(open + 1)..close].Trim();
            }
            catch { }
            foreach (var d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (headsetToken.Length == 0 || !d.FriendlyName.Contains(headsetToken, StringComparison.OrdinalIgnoreCase))
                    return d;
            return mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch { return null; }
    }

    private void StopPlayback() { lock (_playLock) StopPlaybackLocked(); }

    private void StopPlaybackLocked()
    {
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        try { _playReader?.Dispose(); } catch { }
        _player = null; _playReader = null;
    }

    /* ---------------- session grace: close the mic AFTER the reply, once ---------------- */

    private void ScheduleSessionEnd()
    {
        CancelGrace();
        var cts = new CancellationTokenSource();
        _graceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { return; }
            if (!_recorder.IsCapturing) _recorder.EndSession();   // BT returns to music-mode in silence
        });
    }

    private void CancelGrace()
    {
        try { _graceCts?.Cancel(); } catch { }
        _graceCts = null;
    }

    /* ---------------- helpers / feed / status ---------------- */

    private void PostStateFireAndForget(string state)
    {
        _ = _http.PostAsJsonAsync("/api/voice/state", new { state })
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task SpeakAsync(string text, bool isReply = false)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/voice/tts", new { text });
            if (!resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            var wav = await resp.Content.ReadAsByteArrayAsync();
            if (wav.Length > 44) PlayWav(wav, isReply);
        }
        catch { /* announcements are best-effort */ }
    }

    /// <summary>One council member holds the floor at a time: waits for the user to stop
    /// talking AND for any current playback to end before synthesizing with the agent's
    /// voice profile, then holds the floor until its own line finishes playing.</summary>
    private readonly SemaphoreSlim _councilFloor = new(1, 1);
    private int _councilGen;   // bumped on council.cancelled — queued lines from the old session are dropped

    private async Task SpeakCouncilAsync(string text, string? profile)
    {
        var gen = Volatile.Read(ref _councilGen);
        await _councilFloor.WaitAsync();
        try
        {
            if (gen != Volatile.Read(ref _councilGen)) return;  // council was stopped while queued
            for (var waited = 0; _recorder.IsCapturing && waited < 30_000; waited += 500)
                await Task.Delay(500);  // never talk over the user
            for (var waited = 0; _isPlaying && waited < 60_000; waited += 200)
                await Task.Delay(200);  // let the previous speaker finish
            if (gen != Volatile.Read(ref _councilGen)) return;

            var resp = await _http.PostAsJsonAsync("/api/voice/tts", new { text, profile });
            if (!resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            var wav = await resp.Content.ReadAsByteArrayAsync();
            if (wav.Length <= 44) return;
            PlayWav(wav);

            await Task.Delay(300);  // PlaybackStopped race guard
            for (var waited = 0; _isPlaying && waited < 120_000; waited += 200)
                await Task.Delay(200);
            await Task.Delay(350);  // beat between speakers
        }
        catch { /* council speech is best-effort */ }
        finally { _councilFloor.Release(); }
    }

    private static string HeaderText(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var v) ? Uri.UnescapeDataString(v.First()) : "";

    /// <summary>Clipboard requires an STA thread; the hub handler isn't one.</summary>
    private static void SetClipboard(string text)
    {
        var t = new Thread(() => { try { Clipboard.SetText(text); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
    }

    /// <summary>Live event feed: lets JARVIS speak unprompted when agents/briefings/reminders finish.</summary>
    private async Task ConnectEventFeedAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{KernelUrl}/ws")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<JsonElement>("event", async e =>
        {
            try
            {
                var kind = e.GetProperty("kind").GetString() ?? "";
                if (!e.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;

                if (kind == "meeting.detected")
                {
                    var hint = payload.TryGetProperty("hint", out var h) ? h.GetString() : null;
                    _icon.ShowBalloonTip(5000, "Meeting detected", hint ?? "Say 'start meeting mode' to track it.", ToolTipIcon.Info);
                    return;
                }
                if (kind == "wake.state")
                {
                    SetWakeEnabled(payload.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True);
                    return;
                }
                if (kind == "dictation.state")
                {
                    _dictation = payload.TryGetProperty("on", out var don) && don.ValueKind == JsonValueKind.True;
                    if (_dictation)
                        _icon.ShowBalloonTip(4000, "Dictation",
                            $"Typing into: {TextInjector.ForegroundWindowTitle()}", ToolTipIcon.Info);
                    _icon.Text = _dictation ? "JARVIS — DICTATION (say 'stop dictation')" : "JARVIS — hold Right-Ctrl to talk";
                    return;
                }
                if (kind == "dictation.type")
                {
                    var txt = payload.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    if (!string.IsNullOrEmpty(txt)) TextInjector.Type(txt);
                    return;
                }
                if (kind == "meeting.started")
                {
                    var scribeOn = !payload.TryGetProperty("scribe", out var sc) || sc.ValueKind != JsonValueKind.False;
                    if (scribeOn)
                    {
                        (_scribe ??= new MeetingScribe(_http)).Start();
                        _icon.ShowBalloonTip(5000, "Meeting scribe",
                            "Transcribing locally (your mic + what you hear). Nothing leaves this PC.", ToolTipIcon.Info);
                    }
                    return;
                }
                if (kind == "meeting.ended")
                {
                    _scribe?.Stop();
                    return;
                }
                if (kind == "clipboard.set")
                {
                    var clipText = payload.TryGetProperty("text", out var ctx2) ? ctx2.GetString() : null;
                    if (!string.IsNullOrEmpty(clipText))
                    {
                        SetClipboard(clipText);
                        var label = payload.TryGetProperty("label", out var lb) ? lb.GetString() : "text";
                        _icon.ShowBalloonTip(2500, "Copied to clipboard", label ?? "text", ToolTipIcon.Info);
                    }
                    return;
                }
                if (kind.StartsWith("council.", StringComparison.Ordinal))
                {
                    if (kind == "council.cancelled")
                    {
                        // "stop council" silences the floor: drop queued lines, cut the current one.
                        Interlocked.Increment(ref _councilGen);
                        StopPlayback();
                        return;
                    }
                    // Speak only voice-initiated councils — CLI/UI sessions stay silent
                    // (the console shows them; nobody wants test runs talking in their ear).
                    var actor = e.TryGetProperty("actor", out var act) ? act.GetString() : null;
                    if (actor != "voice") return;
                    // The council speaks turn-by-turn, each agent in its own voice profile.
                    if (kind is not ("council.agent.speaking.started" or "council.decision.ready" or "council.user.input.requested" or "council.error")) return;
                    var line = payload.TryGetProperty("spoken", out var cs) ? cs.GetString() : null;
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var profile = payload.TryGetProperty("voiceProfile", out var vp) ? vp.GetString() : null;
                    var speaker = payload.TryGetProperty("displayName", out var dn) ? dn.GetString() : "Moderator";
                    _icon.ShowBalloonTip(4000, $"Council — {speaker}",
                        line.Length > 200 ? line[..200] + "…" : line, ToolTipIcon.None);
                    _ = SpeakCouncilAsync(line, profile);  // semaphore inside serializes the floor
                    return;
                }

                if (!SpokenKinds.Contains(kind)) return;
                var spoken = payload.TryGetProperty("spoken", out var s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(spoken)) return;

                for (var waited = 0; _recorder.IsCapturing && waited < 30_000; waited += 500)
                    await Task.Delay(500); // never talk over the user

                _icon.ShowBalloonTip(5000, "JARVIS", spoken.Length > 200 ? spoken[..200] + "…" : spoken, ToolTipIcon.None);
                await SpeakAsync(spoken);
            }
            catch { /* event feed must never crash the tray */ }
        });

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { await _hub.StartAsync(); return; }
            catch { await Task.Delay(3000); }
        }
    }

    private async Task ShowStatusAsync()
    {
        try
        {
            var h = await _http.GetFromJsonAsync<JsonElement>("/api/system/health");
            _icon.ShowBalloonTip(4000, "JARVIS status",
                $"kernel online · stt {(h.GetProperty("stt").GetBoolean() ? "on" : "off")} · " +
                $"ollama {(h.GetProperty("ollama").GetBoolean() ? "on" : "off")} · " +
                $"tts {h.GetProperty("ttsEngine").GetString()} ({h.GetProperty("voiceProfile").GetString()}) · " +
                $"mic {_recorder.DeviceName} · out {_outputMode}",
                ToolTipIcon.Info);
        }
        catch
        {
            _icon.ShowBalloonTip(3000, "JARVIS status", "Kernel offline.", ToolTipIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        _scribe?.Dispose();
        _wake.Dispose();
        try { _hub?.DisposeAsync().AsTask().Wait(1500); } catch { }
        _hook.Dispose();
        StopPlayback();
        CancelGrace();
        _recorder.EndSession();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
