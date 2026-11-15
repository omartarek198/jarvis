namespace Jarvis.Core;

public record JarvisEvent(string Kind, object? Payload = null, string Actor = "system")
{
    public DateTime At { get; init; } = DateTime.UtcNow;
}

public interface IEventBus
{
    void Publish(JarvisEvent e);
    IDisposable Subscribe(Func<JarvisEvent, Task> handler);
}

public sealed class EventBus : IEventBus
{
    private readonly List<Func<JarvisEvent, Task>> _subs = [];
    private readonly Lock _lock = new();

    public void Publish(JarvisEvent e)
    {
        Func<JarvisEvent, Task>[] subs;
        lock (_lock) subs = [.. _subs];
        foreach (var s in subs)
            _ = Task.Run(async () => { try { await s(e); } catch { /* subscriber errors must not kill the bus */ } });
    }

    public IDisposable Subscribe(Func<JarvisEvent, Task> handler)
    {
        lock (_lock) _subs.Add(handler);
        return new Unsub(() => { lock (_lock) _subs.Remove(handler); });
    }

    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
}
