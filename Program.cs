namespace GameModeTray;

static class Program
{
    const string InstanceMutexName = @"Local\GameModeTray";
    const string ExitEventName = @"Local\GameModeTray.Exit";
    const string Help =
        "GameModeTray.exe [option]\n\n" +
        "--settings\n    Open the advanced device settings at startup\n\n" +
        "--detect-all\n    Allow detecting the commands of any connected device, even if already supported\n\n" +
        "--load-log\n    Offer to load a saved btsnoop log or bug report in the detection, skipping the phone steps\n\n" +
        "--exit\n    Close the running instance\n\n" +
        "--help\n    Show this list";

    /// <summary>"--detect-all": the detection is offered for every connected device.</summary>
    public static bool DetectAll { get; private set; }

    /// <summary>"--load-log": the detection also offers to load a saved log.</summary>
    public static bool LoadLog { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // It is a windowed app with no console to write to
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            ApplicationConfiguration.Initialize();
            MessageBox.Show(Help, "GameModeTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DetectAll = args.Contains("--detect-all");
        LoadLog = args.Contains("--load-log");

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
