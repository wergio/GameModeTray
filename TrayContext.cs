using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Foundation;

namespace GameModeTray;

/// <summary>
/// System tray icon: when the configured headphones connect it enables game mode,
/// checks that it is actually on (when the profile allows it) and retries if needed.
/// </summary>
sealed class TrayContext : ApplicationContext
{
    const int RetryDelayMs = 2000;
    const string AppName = "GameModeTray";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon _tray = new();
    readonly ToolStripLabel _title = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _offItem;
    readonly ToolStripMenuItem _checkItem;
    readonly ToolStripMenuItem _auto = new("Enable automatically on connect") { CheckOnClick = true };
    readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    readonly Icon _iconIdle = MakeIcon(Color.FromArgb(128, 128, 128));
    readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
    readonly Icon _iconWarn = MakeIcon(Color.FromArgb(230, 126, 34));

    readonly AppSettings _settings;
    readonly bool _firstRun;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly SynchronizationContext _ui;
    readonly TypedEventHandler<BluetoothDevice, object> _connectionChanged;

    BluetoothDevice? _device;
    GameModeDevice? _gameDevice;
    SettingsForm? _settingsForm;
    bool _initDone;
    bool _sending;
    bool _verified;
    bool? _gameMode;     // null = state unknown
    string? _error;

    public TrayContext(bool openSettings, WaitHandle exitEvent)
    {
        var saved = AppSettings.Load();
        _firstRun = saved is null;
        _settings = saved ?? new AppSettings();

        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        // The event is raised on a thread-pool thread: marshal back to the UI thread
        _connectionChanged = (_, _) => _ui.Post(_ => OnConnectionChanged(apply: true), null);
        // "GameModeTray.exe --exit" sets the event
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        _offItem = new ToolStripMenuItem("Turn game mode off", null, (_, _) => _ = ApplyAsync(false, automatic: false));
        _checkItem = new ToolStripMenuItem("Check current status", null, (_, _) => _ = CheckStateAsync());
        var menu = new ContextMenuStrip();
        _title.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(_title);
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Turn game mode on", null, (_, _) => _ = ApplyAsync(true, automatic: false));
        menu.Items.Add(_offItem);
        menu.Items.Add(_checkItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_auto);
        menu.Items.Add(_startup);
        menu.Items.Add("Advanced device settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _auto.Checked = _settings.AutoEnable;
        _auto.CheckedChanged += (_, _) => { _settings.AutoEnable = _auto.Checked; _settings.Save(); };
        _startup.Checked = IsInStartup();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _ = ApplyAsync(true, automatic: false);
        // Notifications about a missing or invalid profile lead to the settings
        _tray.BalloonTipClicked += (_, _) => { if (_gameDevice is null) OpenSettings(); };
        _tray.Visible = true;

        Log.Write("Started");
        _ = StartAsync(openSettings);
    }

    bool Connected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    string Title => _settings.Profile.Name.Length > 0 ? _settings.Profile.Name : AppName;

    async Task StartAsync(bool openSettings)
    {
        if (_firstRun) await PickPresetAsync();
        await InitAsync(applyIfConnected: true);
        // Nothing configured yet (first run with headphones that have no preset): ask the user
        if (openSettings || _settings.Profile.DeviceName.Trim().Length == 0) OpenSettings();
    }

    /// <summary>On the first run, uses the preset of headphones already paired with the PC, if any.</summary>
    async Task PickPresetAsync()
    {
        try
        {
            var preset = await PairedDevices.FindPresetAsync(Profile.Presets);
            if (preset is null)
            {
                Log.Write("First run: no paired headphones match a preset");
                return;
            }
            _settings.Profile = preset;
            _settings.Save();
            Log.Write($"First run: using the \"{preset.Name}\" preset");
        }
        catch (Exception ex)
        {
            Log.Write("Preset detection: " + ex.Message);
        }
    }

    /// <summary>Finds the profile's device and starts watching its connection.</summary>
    async Task InitAsync(bool applyIfConnected)
    {
        if (_device is not null) _device.ConnectionStatusChanged -= _connectionChanged;
        _device = null;
        _gameDevice = null;
        _initDone = false;
        _gameMode = null;
        _error = null;
        var profile = _settings.Profile;
        _offItem.Enabled = profile.OffCommand.Trim().Length > 0;
        _checkItem.Enabled = false;
        UpdateTray();

        if (profile.DeviceName.Trim().Length == 0)
        {
            _initDone = true;
            _error = "No headphones configured yet";
            UpdateTray();
            return;
        }
        try
        {
            _device = await PairedDevices.FindAsync(profile.DeviceName);
        }
        catch (Exception ex)
        {
            Log.Write("BluetoothDevice: " + ex.Message);
        }
        _initDone = true;

        if (_device is null)
        {
            Fail($"\"{profile.DeviceName}\" is not paired with this PC (or Bluetooth is off)");
            return;
        }
        try
        {
            _gameDevice = new GameModeDevice(profile, _device.BluetoothAddress, Log.Write);
        }
        catch (FormatException ex)
        {
            Fail("Invalid profile: " + ex.Message);
            return;
        }
        // Without a state query and replies there is nothing to check
        _checkItem.Enabled = _gameDevice.CanReadState;

        Log.Write($"Found {_device.Name} ({_device.BluetoothAddress:X12}), profile \"{profile.Name}\"");
        _device.ConnectionStatusChanged += _connectionChanged;
        OnConnectionChanged(applyIfConnected);
    }

    void Fail(string error)
    {
        _error = error;
        Log.Write(error);
        UpdateTray();
        Notify(error + ". Click here to open the settings.", ToolTipIcon.Warning);
    }

    void OnConnectionChanged(bool apply)
    {
        Log.Write(Connected ? "Headphones connected" : "Headphones not connected");
        _gameMode = null;
        _error = null;
        UpdateTray();
        if (!Connected) return;
        if (apply && _auto.Checked) _ = ApplyAsync(true, automatic: true);
        // Not changing anything: just find out the current state, for the icon
        else _ = ReadStateAsync(waitForConnection: apply);
    }

    async Task ReadStateAsync(bool waitForConnection)
    {
        var device = _gameDevice;
        if (device is null || !device.CanReadState) return;
        // Waits outside the lock, so a menu command in the meantime is not ignored
        if (waitForConnection) await Task.Delay(_settings.Profile.ConnectDelayMs);

        int attempts = Math.Max(1, _settings.Profile.Attempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            // Stop when the headphones went away, or a command already set the state
            if (device != _gameDevice || !Connected || _gameMode is not null) return;
            if (!await _busy.WaitAsync(0)) return;
            string? error;
            try
            {
                bool? state = await Task.Run(device.ReadState);
                error = state is null ? "no state reply from the headphones" : null;
                if (state is not null && device == _gameDevice && Connected)
                {
                    _gameMode = state;
                    _verified = true;
                    Log.Write($"Game mode is {(state.Value ? "ON" : "OFF")} (read on connect)");
                    return;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                _busy.Release();
                UpdateTray();
            }
            Log.Write($"Attempt {attempt} to read the state failed: {error}");
            if (attempt < attempts) await Task.Delay(RetryDelayMs);
        }
    }

    /// <summary>
    /// Menu command: asks the headphones for their current state, e.g. after game mode
    /// was changed from the headphones themselves, which do not report it on their own.
    /// </summary>
    async Task CheckStateAsync()
    {
        var device = _gameDevice;
        if (device is null || !Connected)
        {
            Notify(device is null ? _error ?? "No headphones configured." : "The headphones are not connected.", ToolTipIcon.Warning);
            return;
        }
        if (!device.CanReadState || !await _busy.WaitAsync(0)) return;
        try
        {
            bool? state = await Task.Run(device.ReadState);
            if (device != _gameDevice || !Connected) return;
            if (state is null)
            {
                _error = "no state reply from the headphones";
                Log.Write("Status check failed: " + _error);
                Notify("Could not read the game mode state: " + _error, ToolTipIcon.Warning);
                return;
            }
            if (state != _gameMode) Log.Write($"Game mode is {(state.Value ? "ON" : "OFF")} (status check)");
            _gameMode = state;
            _verified = true;
            _error = null;
            Notify(state.Value ? "Game mode is ON." : "Game mode is OFF.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Write("Status check failed: " + ex.Message);
            Notify("Could not read the game mode state: " + ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _busy.Release();
            UpdateTray();
        }
    }

    async Task ApplyAsync(bool on, bool automatic)
    {
        var device = _gameDevice;
        if (device is null || !Connected)
        {
            if (!automatic) Notify(device is null ? _error ?? "No headphones configured." : "The headphones are not connected.", ToolTipIcon.Warning);
            return;
        }
        if (!await _busy.WaitAsync(0)) return;

        string action = on ? "enable" : "disable";
        try
        {
            _sending = true;
            _error = null;
            UpdateTray();
            if (automatic) await Task.Delay(_settings.Profile.ConnectDelayMs);

            int attempts = Math.Max(1, _settings.Profile.Attempts);
            for (int attempt = 1; attempt <= attempts && Connected; attempt++)
            {
                try
                {
                    bool? state = await Task.Run(() => device.SetGameMode(on));
                    // Without state replies in the profile, a command sent without errors counts as done
                    if (state == on || (state is null && !device.CanVerify))
                    {
                        _gameMode = on;
                        _verified = state is not null;
                        _error = null;
                        // No notification on success: the icon colour shows it
                        Log.Write($"Game mode {(on ? "ON" : "OFF")}{(_verified ? "" : " (not verified)")} (attempt {attempt})");
                        return;
                    }
                    _error = state is null ? "no state reply from the headphones" : "the headphones did not change state";
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                }
                Log.Write($"Attempt {attempt} to {action} failed: {_error}");
                if (attempt < attempts) await Task.Delay(RetryDelayMs);
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

    void OpenSettings()
    {
        if (_settingsForm is not null)
        {
            _settingsForm.Activate();
            return;
        }
        var form = new SettingsForm(_settings.Profile, _iconOn);
        form.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            if (form.DialogResult != DialogResult.OK) return;
            _settings.Profile = form.Result;
            _settings.Save();
            Log.Write($"Settings saved, profile \"{form.Result.Name}\"");
            _ = InitAsync(applyIfConnected: false);
        };
        _settingsForm = form;
        form.Show();
    }

    void UpdateTray()
    {
        (string text, Icon icon) = this switch
        {
            { _initDone: false } => ("Looking for the headphones...", _iconIdle),
            { _gameDevice: null } => (_error ?? "Headphones not found", _iconWarn),
            { Connected: false } => ("Headphones not connected", _iconIdle),
            { _sending: true } => ("Sending command...", _iconIdle),
            { _error: not null } => ("Error: " + _error, _iconWarn),
            { _gameMode: true } => (_verified ? "Game mode ON" : "Game mode ON (not verified)", _iconOn),
            { _gameMode: false } => ("Game mode OFF", _iconWarn),
            _ => ("Headphones connected", _iconIdle),
        };
        _title.Text = Title;
        _status.Text = text;
        _tray.Icon = icon;
        string tip = $"{Title} - {text}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void Notify(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(4000, Title, text, icon);

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
