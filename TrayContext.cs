using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace EdifierGameMode;

/// <summary>
/// System tray icon: when the X2s connect it enables game mode, checks that it
/// is actually on and retries if needed.
/// </summary>
sealed class TrayContext : ApplicationContext
{
    const string DeviceName = "EDIFIER X2s";  // name of the paired classic Bluetooth device
    const int ConnectDelayMs = 3000;          // give the earbuds time to set up the audio link
    const int Attempts = 3;
    const int RetryDelayMs = 2000;

    const string AppName = "EdifierGameMode";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\EdifierGameMode";

    readonly NotifyIcon _tray = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _auto = new("Enable automatically on connect") { CheckOnClick = true };
    readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    readonly Icon _iconIdle = MakeIcon(Color.FromArgb(128, 128, 128));
    readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
    readonly Icon _iconWarn = MakeIcon(Color.FromArgb(230, 126, 34));

    readonly SemaphoreSlim _busy = new(1, 1);
    readonly SynchronizationContext _ui;

    BluetoothDevice? _device;
    X2sClient? _client;
    bool _initDone;
    bool _sending;
    bool? _gameMode;     // null = state unknown
    string? _error;

    public TrayContext()
    {
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Turn game mode on", null, (_, _) => _ = ApplyAsync(true, automatic: false));
        menu.Items.Add("Turn game mode off", null, (_, _) => _ = ApplyAsync(false, automatic: false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_auto);
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _auto.Checked = ReadAutoSetting();
        _auto.CheckedChanged += (_, _) => SaveAutoSetting(_auto.Checked);
        _startup.Checked = IsInStartup();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _ = ApplyAsync(true, automatic: false);
        _tray.Visible = true;
        UpdateTray();

        Log.Write("Started");
        _ = InitAsync();
    }

    bool Connected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    async Task InitAsync()
    {
        try
        {
            // Look the earbuds up among the paired devices by name, so no address is hard-coded
            var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            var info = paired.FirstOrDefault(d => d.Name.StartsWith(DeviceName, StringComparison.OrdinalIgnoreCase));
            if (info is not null) _device = await BluetoothDevice.FromIdAsync(info.Id);
        }
        catch (Exception ex)
        {
            Log.Write("BluetoothDevice: " + ex.Message);
        }
        _initDone = true;

        if (_device is null)
        {
            _error = $"{DeviceName} not paired with this PC (or Bluetooth is off)";
            Log.Write(_error);
            UpdateTray();
            return;
        }
        _client = new X2sClient(_device.BluetoothAddress);
        Log.Write($"Found {_device.Name} ({_device.BluetoothAddress:X12})");
        // The event is raised on a thread-pool thread: marshal back to the UI thread
        _device.ConnectionStatusChanged += (_, _) => _ui.Post(_ => OnConnectionChanged(), null);
        OnConnectionChanged();
    }

    void OnConnectionChanged()
    {
        Log.Write(Connected ? "Earbuds connected" : "Earbuds not connected");
        _gameMode = null;
        _error = null;
        UpdateTray();
        if (Connected && _auto.Checked) _ = ApplyAsync(true, automatic: true);
    }

    async Task ApplyAsync(bool on, bool automatic)
    {
        if (!Connected)
        {
            if (!automatic) Notify("The earbuds are not connected.", ToolTipIcon.Warning);
            return;
        }
        if (!await _busy.WaitAsync(0)) return;

        var client = _client!;   // set together with _device, which Connected requires
        string action = on ? "enable" : "disable";
        try
        {
            _sending = true;
            _error = null;
            UpdateTray();
            if (automatic) await Task.Delay(ConnectDelayMs);

            for (int attempt = 1; attempt <= Attempts && Connected; attempt++)
            {
                try
                {
                    _gameMode = await Task.Run(() => client.SetGameMode(on));
                    if (_gameMode == on)
                    {
                        _error = null;
                        Log.Write($"Game mode {(on ? "ON" : "OFF")} (attempt {attempt})");
                        if (automatic) Notify("Game mode enabled.", ToolTipIcon.Info);
                        return;
                    }
                    _error = "the earbuds did not change state";
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                }
                Log.Write($"Attempt {attempt} to {action} failed: {_error}");
                if (attempt < Attempts) await Task.Delay(RetryDelayMs);
            }
            if (Connected) Notify($"Could not {action} game mode: {_error}", ToolTipIcon.Warning);
        }
        finally
        {
            _sending = false;
            _busy.Release();
            UpdateTray();
        }
    }

    void UpdateTray()
    {
        (string text, Icon icon) = this switch
        {
            { _initDone: false } => ("Looking for earbuds...", _iconIdle),
            { _device: null } => (_error ?? "Earbuds not found", _iconWarn),
            { Connected: false } => ("Earbuds not connected", _iconIdle),
            { _sending: true } => ("Sending command...", _iconIdle),
            { _error: not null } => ("Error: " + _error, _iconWarn),
            { _gameMode: true } => ("Game mode ON", _iconOn),
            { _gameMode: false } => ("Game mode OFF", _iconWarn),
            _ => ("Earbuds connected", _iconIdle),
        };
        _status.Text = text;
        _tray.Icon = icon;
        string tip = "Edifier X2s - " + text;
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void Notify(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(4000, "Edifier X2s", text, icon);

    static bool ReadAutoSetting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("Auto") is not int value || value != 0;
    }

    static void SaveAutoSetting(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("Auto", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    static bool IsInStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is not null;
    }

    static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, false);
    }

    static Icon MakeIcon(Color color)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 0, 0, size.Width - 1, size.Height - 1);
            using var font = new Font("Segoe UI", size.Height * 0.6f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("G", font, Brushes.White, new RectangleF(0, 0, size.Width, size.Height), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        Log.Write("Exiting");
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
