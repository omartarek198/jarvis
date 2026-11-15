using NAudio.CoreAudioApi;
using NAudio.Wave;

Console.WriteLine("=== WASAPI capture endpoints ===");
var mm = new MMDeviceEnumerator();
try
{
    var def = mm.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    Console.WriteLine($"DEFAULT (communications): {def.FriendlyName} [{def.State}]");
    var defc = mm.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    Console.WriteLine($"DEFAULT (console):        {defc.FriendlyName} [{defc.State}]");
}
catch (Exception ex) { Console.WriteLine($"no default capture device: {ex.Message}"); }
foreach (var d in mm.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
    Console.WriteLine($"  {d.FriendlyName} [{d.State}] vol={(d.State == DeviceState.Active ? d.AudioEndpointVolume.MasterVolumeLevelScalar.ToString("F2") : "-")} muted={(d.State == DeviceState.Active ? d.AudioEndpointVolume.Mute.ToString() : "-")}");

Console.WriteLine($"\n=== Legacy WaveIn devices (count={WaveInEvent.DeviceCount}) — recording 1.5s from each ===");
for (int dev = -1; dev < WaveInEvent.DeviceCount; dev++)
{
    var caps = dev >= 0 ? WaveInEvent.GetCapabilities(dev).ProductName : "WAVE_MAPPER (default)";
    try
    {
        using var wi = new WaveInEvent { DeviceNumber = dev, WaveFormat = new WaveFormat(16000, 16, 1) };
        long sumSq = 0; int peak = 0, n = 0;
        wi.DataAvailable += (_, e) =>
        {
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                int s = BitConverter.ToInt16(e.Buffer, i);
                sumSq += (long)s * s; n++;
                if (Math.Abs(s) > peak) peak = Math.Abs(s);
            }
        };
        wi.StartRecording();
        Thread.Sleep(1500);
        wi.StopRecording();
        var rms = n > 0 ? Math.Sqrt((double)sumSq / n) : 0;
        Console.WriteLine($"  dev {dev,2} \"{caps}\": peak={peak} rms={rms:F1} {(peak > 100 ? "<-- LIVE SIGNAL" : "(silent)")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  dev {dev,2} \"{caps}\": ERROR {ex.Message}");
    }
}
