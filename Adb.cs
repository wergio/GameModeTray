using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace GameModeTray;

sealed record AdbResult(int ExitCode, string Output);

enum PhoneState { None, Unauthorized, Offline, Several, Ready }

sealed record PhoneInfo(PhoneState State, string Serial = "", string Model = "", bool Wireless = false);

/// <summary>
/// Android Debug Bridge, used to drive the phone during the detection. Only the official copy
/// that the wizard downloads into the app's data folder is used, not the ones already on the
/// PC: they may be old, and the same version everywhere means the same behaviour.
/// </summary>
sealed partial class Adb(string executable)
{
    const string DownloadUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
    const string SnoopProperty = "persist.bluetooth.btsnooplogmode";

    public const string SnoopFull = "full";
    public const string SnoopDisabled = "disabled";

    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20), ReportTimeout = TimeSpan.FromMinutes(8);

    /// <summary>Local, not roaming: adb is an executable, and the phone's report is large and private.</summary>
    public static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameModeTray");
    static readonly string LocalFolder = Path.Combine(DataFolder, "adb");
    static readonly string LocalPath = Path.Combine(LocalFolder, "adb.exe");

    string _serial = "";

    public string Executable => executable;

    /// <summary>True once the phone was found.</summary>
    public bool HasPhone => _serial.Length > 0;

    /// <summary>The downloaded adb, or null if it was not downloaded yet.</summary>
    public static string? Find() => File.Exists(LocalPath) ? LocalPath : null;

    /// <summary>Downloads the official platform-tools and returns the path of adb.exe.</summary>
    public static async Task<string> DownloadAsync(IProgress<int> percent, CancellationToken cancel)
    {
        Directory.CreateDirectory(LocalFolder);
        string zipPath = Path.Combine(LocalFolder, "platform-tools.zip");
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancel))
            await using (var target = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cancel);
                    done += n;
                    if (total > 0) percent.Report((int)(done * 100 / total));
                }
            }
            await Task.Run(() => Extract(zipPath), cancel);
            return File.Exists(LocalPath) ? LocalPath : throw new IOException("adb.exe is not in the downloaded package.");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>Only adb, its libraries and its license: the package also has tools that are not needed here.</summary>
    static void Extract(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(e => e.FullName.Count(c => c == '/') == 1 &&
                     (e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || e.Name is "adb.exe" or "NOTICE.txt")))
            entry.ExtractToFile(Path.Combine(LocalFolder, entry.Name), overwrite: true);
    }

    async Task<AdbResult> RunAsync(TimeSpan timeout, Action<string>? onLine, CancellationToken cancel, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var output = new StringBuilder();
        void Collect(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            onLine?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        // Not WaitForExit: the adb server it may start would keep the output pipes open
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await exited.Task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("adb did not answer in time.");
        }
        await Task.Delay(100, CancellationToken.None);   // lets the last lines arrive
        lock (output) return new AdbResult(process.ExitCode, output.ToString());
    }

    Task<AdbResult> ShellAsync(params string[] command) =>
        RunAsync(CommandTimeout, null, CancellationToken.None, ["-s", _serial, "shell", .. command]);

    public async Task<string> GetVersionAsync() =>
        ParseVersion((await RunAsync(CommandTimeout, null, CancellationToken.None, "version")).Output);

    /// <summary>The platform-tools version from the "Version 37.0.1-15733141" line, or "" if there is none.</summary>
    public static string ParseVersion(string output) => ToolsVersion().Match(output).Groups[1].Value;

    /// <summary>Looks for the phone and remembers it for the next commands.</summary>
    public async Task<PhoneInfo> FindPhoneAsync()
    {
        var result = await RunAsync(CommandTimeout, null, CancellationToken.None, "devices", "-l");
        var phone = ParseDevices(result.Output);
        _serial = phone.Serial;
        return phone;
    }

    public static PhoneInfo ParseDevices(string output)
    {
        var devices = output.Split('\n').Select(l => l.Trim())
            .SkipWhile(l => !l.StartsWith("List of devices")).Skip(1)
            .Where(l => l.Length > 0 && !l.StartsWith('*'))
            .Select(l => WhiteSpace().Split(l)).Where(d => d.Length >= 2).ToList();

        var ready = devices.Where(d => d[1] == "device").ToList();
        if (ready.Count > 1) return new PhoneInfo(PhoneState.Several);
        if (ready.Count == 1)
        {
            var d = ready[0];
            var model = d.FirstOrDefault(t => t.StartsWith("model:"))?["model:".Length..].Replace('_', ' ') ?? d[0];
            return new PhoneInfo(PhoneState.Ready, d[0], model, d[0].Contains(':') || d[0].Contains("adb-tls-connect"));
        }
        if (devices.Any(d => d[1] == "unauthorized")) return new PhoneInfo(PhoneState.Unauthorized);
        return new PhoneInfo(devices.Count > 0 ? PhoneState.Offline : PhoneState.None);
    }

    /// <summary>The configured log mode. Recent Android versions hide it from adb and return nothing.</summary>
    public async Task<string> GetSnoopModeAsync() => (await ShellAsync("getprop", SnoopProperty)).Output.Trim();

    /// <summary>False if the phone refuses, as recent Android versions do: only the Developer options can set it.</summary>
    public async Task<bool> SetSnoopModeAsync(string mode)
    {
        var result = await ShellAsync("setprop", SnoopProperty, mode);
        return result.ExitCode == 0 && !result.Output.Contains("Failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mode in use since Bluetooth last started, in upper case: a changed setting only counts
    /// from the next start. Empty on Android versions that do not report it.
    /// </summary>
    public async Task<string> GetActiveSnoopModeAsync()
    {
        // The report takes a few seconds to include it after Bluetooth starts
        for (int i = 0; i < 6; i++)
        {
            var mode = ParseSnoopMode((await ShellAsync("dumpsys", "bluetooth_manager")).Output);
            if (mode.Length > 0) return mode;
            await Task.Delay(1000);
        }
        return "";
    }

    public static string ParseSnoopMode(string dump) => ActiveSnoopMode().Match(dump).Groups[1].Value.ToUpperInvariant();

    async Task<bool?> IsBluetoothOnAsync() => (await ShellAsync("settings", "get", "global", "bluetooth_on")).Output.Trim() switch
    {
        "0" => false,
        "1" => true,
        _ => null,
    };

    /// <summary>Turns the phone's Bluetooth on or off; false if that could not be done or confirmed.</summary>
    public async Task<bool> SetBluetoothAsync(bool on)
    {
        string action = on ? "enable" : "disable";
        // Which one works depends on the Android version
        foreach (var command in new[] { new[] { "svc", "bluetooth", action }, ["cmd", "bluetooth_manager", action] })
        {
            await ShellAsync(command);
            for (int i = 0; i < 14; i++)
            {
                await Task.Delay(500);
                if (await IsBluetoothOnAsync() == on) return true;
            }
        }
        return false;
    }

    public async Task<bool> RestartBluetoothAsync() => await SetBluetoothAsync(false) && await SetBluetoothAsync(true);

    /// <summary>
    /// Whether the phone knows these headphones and, if the phone reports it, whether they are
    /// connected now (null if it does not say).
    /// </summary>
    public async Task<(bool Known, bool? Connected)> ReadDeviceAsync(string name, string address)
    {
        var dump = (await ShellAsync("dumpsys", "bluetooth_manager")).Output;
        return (KnowsDevice(dump, name, address), ParseConnected(dump, address));
    }

    public static bool KnowsDevice(string dump, string name, string address) =>
        dump.Contains(name, StringComparison.OrdinalIgnoreCase) ||
        dump.Contains(address, StringComparison.OrdinalIgnoreCase) ||
        dump.Contains("XX:XX:XX:XX:" + address[^5..], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The state in the device's A2DP section: "A2dpStateMachine for XX:XX:XX:XX:87:F9" and, a
    /// few lines below, "mConnectionState: CONNECTED". Null if there is no such section.
    /// </summary>
    public static bool? ParseConnected(string dump, string address)
    {
        var lines = dump.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("A2dpStateMachine for") || !lines[i].Contains(address[^5..], StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in lines.Skip(i + 1).Take(8))
            {
                var match = ConnectionState().Match(line);
                if (match.Success) return match.Groups[1].Value == "CONNECTED";
            }
        }
        return null;
    }

    /// <summary>Saves the phone's bug report, which holds the Bluetooth log.</summary>
    public async Task SaveBugReportAsync(string path, IProgress<int> percent, CancellationToken cancel)
    {
        var result = await RunAsync(ReportTimeout, line =>
        {
            var match = ReportProgress().Match(line);
            if (match.Success) percent.Report(int.Parse(match.Groups[1].Value));
        }, cancel, "-s", _serial, "bugreport", path);
        if (result.ExitCode != 0 || !File.Exists(path))
            throw new IOException("The phone did not produce the report: " + result.Output.Trim());
    }

    /// <summary>Leaves nothing running, and adb.exe free to be replaced.</summary>
    public Task StopServerAsync() => RunAsync(CommandTimeout, null, CancellationToken.None, "kill-server");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\bmConnectionState:\s*(\w+)")]
    private static partial Regex ConnectionState();

    [GeneratedRegex(@"\bsSnoopLogSettingAtEnable\s*=\s*(\w+)")]
    private static partial Regex ActiveSnoopMode();

    [GeneratedRegex(@"^Version ([\d.]+)", RegexOptions.Multiline)]
    private static partial Regex ToolsVersion();

    [GeneratedRegex(@"\[\s*(\d+)%\]")]
    private static partial Regex ReportProgress();
}
