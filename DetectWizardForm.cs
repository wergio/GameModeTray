using System.Diagnostics;
using System.Media;
using Windows.Devices.Bluetooth;

namespace GameModeTray;

/// <summary>
/// Guides the user from the Android tools to a working profile, one step at a time: each step
/// has buttons that do the job or check it, and Next unlocks once it is done.
/// </summary>
sealed class DetectWizardForm : Form
{
    const string SnoopReminder = "Important: when you are done, set the Bluetooth HCI snoop log back to Disabled in the phone's " +
        "Developer options. This phone does not let the app do it, and until then it keeps recording its Bluetooth traffic.";
    // Odd, to end with game mode on; 5 is the fewest that CommandFinder can recognise
    const int Toggles = 5, ToggleSeconds = 6, GetReadySeconds = 4;
    const int StepTest = 6;

    static readonly Color PassColor = Color.FromArgb(0, 120, 40), FailColor = Color.Firebrick;

    readonly ScannedDevice _device;
    readonly (string Name, Action Show)[] _steps;
    readonly Label[] _stepLabels;
    readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    readonly TextBox _body = new()
    {
        Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, TabStop = false,
        BackColor = SystemColors.Control, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
    };
    readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
    readonly Label _reminder = new()
    {
        AutoSize = true, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(6),
        BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText, Text = SnoopReminder,
    };
    readonly ProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Visible = false, Margin = new Padding(0, 0, 0, 6) };
    readonly FlowLayoutPanel _actions = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
    readonly TextBox _output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Consolas", 9f), Visible = false,
        Margin = new Padding(0, 6, 0, 0),
    };
    readonly Button _back = new() { Text = "< Back", AutoSize = true };
    readonly Button _next = new() { Text = "Next >", AutoSize = true };
    readonly Button _stop = new() { Text = "Stop", AutoSize = true, Visible = false };
    readonly Windows.Foundation.TypedEventHandler<BluetoothDevice, object> _connectionChanged;
    readonly Font _normalFont;

    Adb? _adb;
    FinderResult? _found;
    CancellationTokenSource? _work;
    Button? _download;
    Action? _refresh;          // brings the page up to date, e.g. after the headphones connect
    string? _snoopBefore;      // the phone's setting before the wizard changed it
    bool _snoopChanged, _snoopByHand, _phoneBluetoothOff, _passed, _busy;
    int _step, _pair, _reached;

    static Task _cleanUp = Task.CompletedTask;

    public Profile? Result { get; private set; }

    public DetectWizardForm(ScannedDevice device, Icon icon)
    {
        _device = device;
        Text = "GameModeTray - Detect commands of " + device.Name;
        Icon = icon;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(600));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(500));
        _normalFont = Font;
        _title.Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold);
        _status.Font = new Font(Font, FontStyle.Bold);
        _status.MaximumSize = new Size(LogicalToDeviceUnits(520), 0);
        _reminder.Font = _status.Font;
        _reminder.MaximumSize = new Size(LogicalToDeviceUnits(570), 0);
        _progress.Height = LogicalToDeviceUnits(14);
        _output.Height = LogicalToDeviceUnits(100);

        _steps =
        [
            ("Android tools", ShowTools),
            ("Phone", ShowPhone),
            ("Bluetooth log", ShowLog),
            ("Headphones", ShowHeadphones),
            ("Recording", ShowRecording),
            ("Analysis", ShowAnalysis),
            ("Test on the PC", ShowTest),
        ];

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Width = LogicalToDeviceUnits(150), Padding = new Padding(10, 14, 6, 6),
        };
        _stepLabels = _steps.Select(_ => new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 8) }).ToArray();
        sidebar.Controls.AddRange(_stepLabels);

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 12, 12, 6) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(_title, 0, 0);
        page.Controls.Add(_reminder, 0, 1);
        page.Controls.Add(_body, 0, 2);
        page.Controls.Add(_progress, 0, 3);
        page.Controls.Add(_status, 0, 4);
        page.Controls.Add(_actions, 0, 5);
        page.Controls.Add(_output, 0, 6);

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        CancelButton = close;
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => Go(_step + 1);
        _stop.Click += (_, _) => _work?.Cancel();
        var navigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 6, 10, 10) };
        navigation.Controls.AddRange([close, _next, _back, _stop]);

        Controls.Add(page);
        Controls.Add(sidebar);
        Controls.Add(navigation);

        // The event is raised on a thread-pool thread
        _connectionChanged = (_, _) => { if (IsHandleCreated) BeginInvoke(new Action(() => _refresh?.Invoke())); };
        device.Device.ConnectionStatusChanged += _connectionChanged;
        FormClosing += (_, _) =>
        {
            if (_snoopByHand) MessageBox.Show(this, SnoopReminder, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            CleanUp();
        };

        Go(0);
    }

    bool Connected => _device.Device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // ---- Page building blocks

    void Go(int step)
    {
        _step = step;
        _passed = false;
        _refresh = null;
        _body.Font = _normalFont;
        _status.Text = "";
        _reminder.Visible = _snoopByHand && _found is not null;
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Continuous;
        _actions.Controls.Clear();
        for (int i = 0; i < _stepLabels.Length; i++)
        {
            _stepLabels[i].Text = (i < _reached ? "✓ " : "    ") + $"{i + 1}. {_steps[i].Name}";
            _stepLabels[i].Font = new Font(_normalFont, i == step ? FontStyle.Bold : FontStyle.Regular);
            _stepLabels[i].ForeColor = i <= _reached || i == step ? SystemColors.ControlText : SystemColors.GrayText;
        }
        _steps[step].Show();
        UpdateNavigation();
    }

    void ShowPage(string title, string body)
    {
        _title.Text = title;
        _body.Text = body.Replace("\n", Environment.NewLine);
    }

    Button Action(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 4) };
        button.Click += (_, _) => onClick();
        _actions.Controls.Add(button);
        return button;
    }

    void UpdateNavigation()
    {
        _back.Enabled = !_busy && _step > 0;
        _next.Enabled = !_busy && _passed;
        _next.Visible = _step < StepTest;
        _stop.Visible = _busy && _work is not null;
    }

    void Say(string text, Color color)
    {
        if (IsDisposed) return;
        _status.ForeColor = color;
        _status.Text = text;
    }

    void Say(string text) => Say(text, SystemColors.ControlText);

    void Pass(string text)
    {
        if (IsDisposed) return;
        Say("✓ " + text, PassColor);
        _passed = true;
        if (_step >= _reached)
        {
            _reached = _step + 1;
            _stepLabels[_step].Text = $"✓ {_step + 1}. {_steps[_step].Name}";
        }
        UpdateNavigation();
    }

    void Fail(string text)
    {
        if (IsDisposed) return;
        Say("✗ " + text, FailColor);
        _passed = false;
        UpdateNavigation();
    }

    void Append(string line)
    {
        if (IsDisposed) return;
        _output.Visible = true;
        _output.AppendText(line + Environment.NewLine);
    }

    void AppendFromAnyThread(string line)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Append(line)));
    }

    void ShowProgress(int percent)
    {
        if (IsDisposed) return;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Clamp(percent, 0, 100);
        _progress.Visible = true;
    }

    /// <summary>Runs a job with the buttons locked. With canStop the Stop button can interrupt it.</summary>
    async Task Work(Func<CancellationToken, Task> job, bool canStop = false)
    {
        using var work = new CancellationTokenSource();
        if (canStop) _work = work;
        _busy = true;
        foreach (Control c in _actions.Controls) c.Enabled = false;
        UpdateNavigation();
        try
        {
            await job(work.Token);
        }
        catch (OperationCanceledException)
        {
            Say("Stopped.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            _work = null;
            _busy = false;
            if (!IsDisposed)
            {
                foreach (Control c in _actions.Controls) c.Enabled = true;
                UpdateNavigation();
                _refresh?.Invoke();
            }
        }
    }

    // ---- Step 1: Android tools

    void ShowTools()
    {
        ShowPage("Android tools",
            "The commands are sent by the headphones' companion app, so they are recorded on an Android phone " +
            "(iPhones cannot do this). This app drives the phone with adb, the official Android tool.\n\n" +
            "You will need an Android phone with the companion app of your headset installed, and a USB cable.\n\n" +
            "The procedure needs the Android platform-tools from Google (from dl.google.com). Click to download them if they are not present.");
        _download = Action("Download adb", () => _ = Work(DownloadAsync, canStop: true));
        if (Program.LoadLog) Action("Load a saved log", ChooseLog);
        ShowTools(Adb.Find());
    }

    /// <summary>With "--load-log": reads a log kept aside and goes straight to the test.</summary>
    void ChooseLog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Bugreport or btsnoop log (*.zip;*.log)|*.zip;*.log|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "btsnoop"),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (Analyze(BtSnoopLog.Load(dialog.FileName))) Go(StepTest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            Fail(ex.Message);
        }
    }

    void ShowTools(string? path)
    {
        _adb = path is null ? null : new Adb(path);
        _download!.Text = path is null ? "Download adb" : "Download again";
        if (path is null) Fail("adb has not been downloaded yet.");
        else _ = CheckAdbAsync(_adb!);
    }

    /// <summary>Runs adb to read its version, which also shows that the copy works.</summary>
    async Task CheckAdbAsync(Adb adb)
    {
        Say("Checking adb...");
        try
        {
            var version = await adb.GetVersionAsync();
            if (_step != 0 || adb != _adb) return;
            if (version.Length > 0) Pass($"adb {version} is ready: {adb.Executable}");
            else Fail("adb does not answer as expected: download it again.");
        }
        catch (Exception ex) when (_step == 0)
        {
            Append(ex.Message);
            Fail("adb does not start: download it again.");
        }
    }

    async Task DownloadAsync(CancellationToken cancel)
    {
        Say("Downloading adb...");
        ShowProgress(0);
        try
        {
            if (_adb is not null) await _adb.StopServerAsync();   // it would keep adb.exe locked
            ShowTools(await Adb.DownloadAsync(new Progress<int>(ShowProgress), cancel));
        }
        finally
        {
            if (!IsDisposed) _progress.Visible = false;
        }
    }

    // ---- Step 2: Phone

    void ShowPhone()
    {
        ShowPage("Connect the phone",
            "1. On the phone open Settings > About phone and tap Build number 7 times to unlock the Developer options.\n" +
            "2. In the Developer options turn on USB debugging.\n" +
            "3. Connect the phone to this PC with a USB cable. Use the cable, not wireless debugging.\n" +
            "4. Unlock the phone and accept the \"Allow USB debugging?\" prompt, then press Check the phone.");
        Action("Check the phone", () => _ = Work(_ => CheckPhoneAsync()));
    }

    async Task CheckPhoneAsync()
    {
        Say("Looking for the phone...");
        var phone = await _adb!.FindPhoneAsync();
        switch (phone.State)
        {
            case PhoneState.Ready when phone.Wireless:
                Pass($"{phone.Model} found, over wireless debugging: it should work, but the cable is more reliable.");
                break;
            case PhoneState.Ready:
                Pass($"{phone.Model} is connected.");
                break;
            case PhoneState.Unauthorized:
                Fail("The phone is waiting: unlock it, accept the \"Allow USB debugging?\" prompt and check again.");
                break;
            case PhoneState.Offline:
                Fail("The phone does not respond: unplug it, plug it back in and check again.");
                break;
            case PhoneState.Several:
                Fail("More than one Android device is connected: leave only this phone.");
                break;
            default:
                Fail("No phone found. Check the USB cable and that USB debugging is on. If the phone asks, choose file transfer.");
                break;
        }
    }

    // ---- Step 3: Bluetooth log

    void ShowLog()
    {
        ShowPage("Turn on the Bluetooth log",
            "The phone has to record its Bluetooth traffic.\n\n" +
            "1. Press Turn on the log: it tries to set the HCI snoop log for you, but many phones do not allow it.\n" +
            "2. If it fails, do it by hand: in the Developer options set Bluetooth HCI snoop log to Enabled (not Filtered).\n" +
            "3. Press Restart Bluetooth and check: the log only starts when Bluetooth restarts, so the headphones will drop for a moment.");
        Action("Turn on the log", () => _ = Work(_ => EnableLogAsync()));
        Action("Restart Bluetooth and check", () => _ = Work(_ => RestartAndCheckAsync()));
    }

    async Task EnableLogAsync()
    {
        var adb = _adb!;
        Say("Turning on the log...");
        var mode = await adb.GetSnoopModeAsync();
        if (await adb.SetSnoopModeAsync(Adb.SnoopFull))
        {
            if (!_snoopChanged) _snoopBefore = mode;
            _snoopChanged = true;
            Say("The log is set: now press Restart Bluetooth and check.");
        }
        else
        {
            Fail("This phone does not allow it: set it by hand in the Developer options, then press Restart Bluetooth and check.");
        }
    }

    async Task RestartAndCheckAsync()
    {
        var adb = _adb!;
        Say("Restarting the phone's Bluetooth...");
        bool restarted = await adb.RestartBluetoothAsync();
        Say("Checking the log...");
        var mode = await adb.GetActiveSnoopModeAsync();
        if (mode.Length == 0) mode = (await adb.GetSnoopModeAsync()).ToUpperInvariant();

        _snoopByHand = !_snoopChanged;
        if (mode == "FULL") Pass("The log is on.");
        else if (mode.Contains("FILTER")) Fail("The log is set to Filtered, which removes the data: set it to Enabled, then press Restart Bluetooth and check.");
        else if (!restarted) Fail("Bluetooth could not be restarted: turn it off and on on the phone, then press Restart Bluetooth and check.");
        else if (mode.Length > 0) Fail("The log is off: set it to Enabled in the Developer options, then press Restart Bluetooth and check.");
        else Pass("This phone does not say how the log is set: going on, the analysis will tell.");
    }

    // ---- Step 4: Headphones

    void ShowHeadphones()
    {
        ShowPage("Move the headphones to the phone",
            $"1. Disconnect {_device.Name} from this PC: in the Bluetooth settings choose Disconnect.\n" +
            "2. Connect them to the phone, and check that sound plays there.\n" +
            "3. Press Check headset connected to phone. Do not open the companion app yet.");
        Action("Open PC Bluetooth settings", () => Open("ms-settings:bluetooth"));
        Action("Check headset connected to phone", () => _ = Work(_ => CheckHeadphonesAsync()));
    }

    async Task CheckHeadphonesAsync()
    {
        if (Connected)
        {
            Fail($"{_device.Name} are still connected to this PC.");
            return;
        }
        Say("Asking the phone...");
        var (known, connected) = await _adb!.ReadDeviceAsync(_device.Name, _device.AddressText);
        if (connected == true) Pass($"{_device.Name} are connected to the phone.");
        else if (connected == false) Fail($"{_device.Name} are not connected to the phone: connect them, and check that sound plays there.");
        else if (known) Pass("Disconnected from the PC. This phone does not say if they are connected to it: check that sound plays there.");
        else Fail($"The phone does not seem to know {_device.Name}: pair them with it first.");
    }

    // ---- Step 5: Recording

    void ShowRecording()
    {
        ShowPage("Record the changes",
            "1. Open the companion app and set game mode OFF if it's ON.\n" +
            "2. Close the app completely, open it again and wait 5 seconds.\n\n" +
            $"Then press Start: this window tells you when to change game mode, {Toggles} times, with a sound at each change. " +
            "Keep the phone at hand and change nothing else in the app.");
        Action("Start", () => _ = Work(RecordAsync, canStop: true));
    }

    async Task RecordAsync(CancellationToken cancel)
    {
        _actions.Controls.Clear();
        _body.Font = new Font(_normalFont.FontFamily, _normalFont.Size * 2, FontStyle.Bold);
        _progress.Visible = true;

        async Task Countdown(string title, string instruction, int seconds)
        {
            ShowPage(title, instruction);
            for (int left = seconds; left > 0; left--)
            {
                Say($"Next in {left}...");
                ShowProgress(100 * (seconds - left) / seconds);
                await Task.Delay(1000, cancel);
            }
        }

        try
        {
            await Countdown("Get ready", "Game mode must be OFF.", GetReadySeconds);
            for (int i = 1; i <= Toggles; i++)
            {
                SystemSounds.Asterisk.Play();
                await Countdown($"Change {i} of {Toggles}", $"Turn game mode {(i % 2 == 1 ? "ON" : "OFF")}", ToggleSeconds);
            }
        }
        catch (OperationCanceledException) when (!IsDisposed)
        {
            Go(_step);   // back to the start of the recording
            throw;
        }

        _body.Font = _normalFont;
        _progress.Visible = false;
        SystemSounds.Asterisk.Play();
        ShowPage("Last part", "Leave game mode ON.\n\nClose the companion app completely, open it again, wait 5 seconds, then press Done.");
        Say("");
        Action("Done", () => Pass("Recording done, go to the next step."));
    }

    // ---- Step 6: Analysis

    void ShowAnalysis()
    {
        ShowPage("Get and analyze the log",
            "The log is inside the phone's bug report, which takes 1 to 3 minutes to prepare: keep the phone connected " +
            "and click Get the log.\n\n" +
            "A bug report holds a lot of data about the phone: only the Bluetooth log is read, and the report is deleted right after.");
        Action("Get the log", () => _ = Work(GetLogAsync, canStop: true));
    }

    async Task GetLogAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(Adb.DataFolder);
        string report = Path.Combine(Adb.DataFolder, "bugreport.zip");
        try
        {
            Say("The phone is preparing the report...");
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = true;
            await _adb!.SaveBugReportAsync(report, new Progress<int>(ShowProgress), cancel);
            Say("Reading the log...");
            var log = await Task.Run(() => BtSnoopLog.Load(report), cancel);
            if (Analyze(log))
            {
                await RestoreLogAsync();
                StopAdbServer();   // the phone's part is over: it restarts by itself if needed
            }
        }
        finally
        {
            File.Delete(report);
            if (!IsDisposed) _progress.Visible = false;
        }
    }

    /// <summary>Looks for the commands in the log and says how it went.</summary>
    bool Analyze(BtSnoopLog log)
    {
        var found = CommandFinder.Analyze(log, _device.Address);
        Append($"{found.MessageCount} RFCOMM messages in the log.");
        if (found.Problem is not null)
        {
            Fail(found.Problem);
            return false;
        }
        _found = found;
        _pair = 0;
        _reminder.Visible = _snoopByHand;
        if (!found.AddressFound) Append($"Warning: {_device.Name} is not in the log, all its traffic was analyzed.");
        foreach (var pair in found.Pairs)
            Append($"Candidate on channel {pair.Channel}: ON {Hex.Format(pair.On)}, OFF {Hex.Format(pair.Off)} ({pair.Flips} changes)");
        foreach (var query in found.Queries) Append("Possible state query: " + Hex.Format(query));
        Pass(found.Pairs.Count == 1 ? "Found a pair of commands." : $"Found {found.Pairs.Count} possible pairs of commands.");
        return true;
    }

    async Task RestoreLogAsync()
    {
        if (!_snoopChanged || _adb is null) return;
        _snoopChanged = false;
        await _adb.SetSnoopModeAsync(string.IsNullOrEmpty(_snoopBefore) ? Adb.SnoopDisabled : _snoopBefore);
    }

    // ---- Step 7: Test on the PC

    void ShowTest()
    {
        var pair = _found?.Pairs[_pair];
        if (pair is null) return;
        ShowPage("Try the commands from this PC",
            $"Candidate {_pair + 1} of {_found!.Pairs.Count}, RFCOMM channel {pair.Channel}.\n\n" +
            $"1. Turn off the phone's Bluetooth, so that it lets go of the headphones.\n" +
            $"2. Connect {_device.Name} to this PC.\n" +
            "3. Press Run the test: it turns game mode off and on twice, sending only packets seen in the log, and leaves it on. " +
            "It takes about 20 seconds, because headphones ignore commands that come too close together.");
        if (_adb?.HasPhone == true) Action("Turn off phone Bluetooth", () => _ = Work(_ => TurnOffPhoneBluetoothAsync()));
        Action("Open PC Bluetooth settings", () => Open("ms-settings:bluetooth"));
        var run = Action("Run the test", () => _ = Work(_ => RunTestAsync()));
        _refresh = () =>
        {
            run.Enabled = Connected;
            Say(Connected ? $"{_device.Name} are connected to this PC." : $"Waiting for {_device.Name} to connect to this PC...");
        };
        _refresh();
    }

    async Task TurnOffPhoneBluetoothAsync()
    {
        Say("Turning off the phone's Bluetooth...");
        _phoneBluetoothOff = await _adb!.SetBluetoothAsync(false);
        if (!_phoneBluetoothOff) Append("Could not turn off the phone's Bluetooth: do it on the phone.");
        StopAdbServer();
    }

    async Task RunTestAsync()
    {
        var pair = _found!.Pairs[_pair];
        var queries = _found.Queries;
        try
        {
            var outcome = await Task.Run(() =>
            {
                using var probe = new RfcommProbe(_device.Address, pair.Channel, AppendFromAnyThread);
                return CommandTester.Run(probe.Exchange, pair, queries);
            });
            if (IsDisposed) return;
            if (outcome.Verified) ShowDone(pair, outcome, "The headphones confirmed every change of state.");
            else ShowAsk(pair, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append("Error: " + ex.Message);
        }
    }

    void ShowAsk(CommandPair pair, TestOutcome outcome)
    {
        _refresh = null;
        _actions.Controls.Clear();
        Say("");
        ShowPage("Did game mode change?",
            "The commands were sent, but the headphones gave no reply that tells on from off, so only you can tell.\n\n" +
            "Did you hear the headphones react, with a tone or a voice? You can send the commands again with the buttons below.");
        Action("Send ON", () => _ = Work(_ => SendAsync(pair, pair.On)));
        Action("Send OFF", () => _ = Work(_ => SendAsync(pair, pair.Off)));
        Action("Yes, it works", () => ShowDone(pair, outcome, "Without state replies, a command will count as done once it is sent."));
        Action("No, try the next", NextPair);
    }

    async Task SendAsync(CommandPair pair, byte[] command)
    {
        try
        {
            await Task.Run(() =>
            {
                using var probe = new RfcommProbe(_device.Address, pair.Channel, AppendFromAnyThread);
                probe.Exchange(command, 500, true);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append("Error: " + ex.Message);
        }
    }

    void NextPair()
    {
        if (++_pair < _found!.Pairs.Count) Go(StepTest);
        else ShowFailed();
    }

    void ShowDone(CommandPair pair, TestOutcome outcome, string note)
    {
        var profile = new Profile
        {
            Name = _device.Name,
            DeviceName = _device.Name,
            Channel = pair.Channel,
            OnCommand = Hex.Format(pair.On),
            OffCommand = Hex.Format(pair.Off),
            StateQuery = outcome.Query,
            StateOnReply = outcome.OnReply,
            StateOffReply = outcome.OffReply,
        };
        _refresh = null;
        _actions.Controls.Clear();
        ShowPage("Commands found", note + "\n\n" +
            $"RFCOMM channel: {profile.Channel}\nON command: {profile.OnCommand}\nOFF command: {profile.OffCommand}\n" +
            $"State query: {Or(profile.StateQuery)}\nState reply when ON: {Or(profile.StateOnReply)}\n" +
            $"State reply when OFF: {Or(profile.StateOffReply)}\n\n" +
            "ON is the last command of the recording. If they turn out swapped, exchange them in the settings.");
        Pass("Done.");
        Action("Use these values", () =>
        {
            Result = profile;
            DialogResult = DialogResult.OK;
            Close();
        });
    }

    static string Or(string text) => text.Length > 0 ? text : "none";

    void ShowFailed()
    {
        _refresh = null;
        _actions.Controls.Clear();
        ShowPage("The commands were not found",
            "None of the candidates worked. The headphones may use a session handshake or encryption, which cannot be replayed. " +
            "You can save a report of this attempt, or go through the log by hand with Option B of the guide.");
        Fail("No working commands.");
        Action("Save the report...", SaveReport);
    }

    void SaveReport()
    {
        using var dialog = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "gamemodetray-detection.txt" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, $"{_device.Name} ({_device.LinkText}){Environment.NewLine}{_output.Text}");
    }

    /// <summary>
    /// Leaves the phone as it was found, whatever step the wizard is closed at. It runs in the
    /// background so that the window closes at once: see <see cref="WaitForCleanUp"/>.
    /// </summary>
    void CleanUp()
    {
        _device.Device.ConnectionStatusChanged -= _connectionChanged;
        _refresh = null;
        _work?.Cancel();
        var adb = _adb;
        if (adb is null) return;
        bool bluetooth = _phoneBluetoothOff;
        var restoreLog = RestoreLogAsync;
        _cleanUp = Task.Run(async () =>
        {
            try
            {
                await restoreLog();
                if (bluetooth) await adb.SetBluetoothAsync(true);
                await adb.StopServerAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Detection clean up: " + ex.Message);
            }
        });
    }

    /// <summary>Called when the app exits: the process must not end while the phone is being restored.</summary>
    public static void WaitForCleanUp() => _cleanUp.Wait(TimeSpan.FromSeconds(10));

    /// <summary>The adb server is not needed for now: stops it without holding up the next step.</summary>
    void StopAdbServer()
    {
        var adb = _adb;
        if (adb is null) return;
        _ = Task.Run(async () =>
        {
            try { await adb.StopServerAsync(); }
            catch (Exception ex) { Log.Write("Stopping adb: " + ex.Message); }
        });
    }
}
