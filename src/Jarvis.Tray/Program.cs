namespace Jarvis.Tray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single instance, enforced: two trays = every reply spoken twice (the "echo" bug).
        using var mutex = new Mutex(true, @"Global\JarvisTraySingleton", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        GC.KeepAlive(mutex);
    }
}
