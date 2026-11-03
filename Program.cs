namespace GameModeTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Single instance: a second launch (e.g. double-clicking the exe) exits immediately
        using var mutex = new Mutex(true, @"Local\GameModeTray", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(openSettings: args.Contains("--settings")));
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
