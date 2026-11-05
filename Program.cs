namespace GameModeTray;

static class Program
{
    const string InstanceMutexName = @"Local\GameModeTray";
    const string ExitEventName = @"Local\GameModeTray.Exit";

    [STAThread]
    static void Main(string[] args)
    {
        // "--exit" asks the running instance to quit (used by tools\publish.ps1 -Install)
        bool exitRequested = args.Contains("--exit");
        // Single instance: a second launch (e.g. double-clicking the exe) exits immediately
        using var mutex = new Mutex(!exitRequested, InstanceMutexName, out bool first);
        if (exitRequested)
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitSignal))
                using (exitSignal) exitSignal.Set();
            return;
        }
        if (!first) return;

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(openSettings: args.Contains("--settings"), exitEvent));
    }
}

/// <summary>Minimal log in %LOCALAPPDATA%\GameModeTray\log.txt, handy when something goes wrong.</summary>
static class Log
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameModeTray");
    static readonly string FilePath = Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
