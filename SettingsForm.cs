using System.Diagnostics;
using Windows.Devices.Bluetooth;

namespace GameModeTray;

/// <summary>Edits the headphone profile and lets the user try the commands before saving.</summary>
sealed class SettingsForm : Form
{
    const string GuideUrl = "https://github.com/wergio/GameModeTray#adding-support-for-other-headphones";
    const string JsonFilter = "Profile (*.json)|*.json";
    const string Custom = "Custom";

    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox _name = NewBox(false);
    readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    readonly NumericUpDown _channel = NewNumber(1, 30, 1);
    readonly TextBox _on = NewBox(true), _off = NewBox(true), _query = NewBox(true);
    readonly TextBox _onReply = NewBox(true), _offReply = NewBox(true);
    readonly NumericUpDown _delay = NewNumber(0, 30000, 500), _attempts = NewNumber(1, 10, 1);
    readonly TextBox _output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9f),
    };
    readonly Button[] _testButtons;
    bool _filling;   // true while the fields are filled in by code, not by the user

    public Profile Result { get; private set; }

    public SettingsForm(Profile profile, Icon icon)
    {
        Result = profile;
        Text = "GameModeTray - Advanced device settings";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(760));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(560));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddWide(grid, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(590), 0),
            Margin = new Padding(3, 0, 3, 10),
            Text = "GameModeTray turns on the game mode of your headphones every time they connect. " +
                   "Pick your headphones from the presets. If they are not listed, enter the commands " +
                   "their companion app sends (see the guide below) and try them with the test buttons before saving.",
        });

        _preset.Items.Add(Custom);
        _preset.Items.AddRange(Profile.Presets.Cast<object>().ToArray());
        _preset.SelectedIndexChanged += (_, _) =>
        {
            // "Custom" leaves the fields as they are
            if (_filling || _preset.SelectedItem is not Profile preset) return;
            Fill(preset);
            Append($"Loaded the \"{preset.Name}\" preset.");
        };
        AddRow(grid, "Preset", _preset);
        AddRow(grid, "Profile name", _name);
        AddRow(grid, "Bluetooth device", _device);
        AddRow(grid, "RFCOMM channel", _channel);
        AddRow(grid, "ON command", _on);
        AddRow(grid, "OFF command (optional)", _off);
        AddRow(grid, "State query (optional)", _query);
        AddRow(grid, "State reply when ON (optional)", _onReply);
        AddRow(grid, "State reply when OFF (optional)", _offReply);
        AddRow(grid, "Delay after connect (ms)", _delay);
        AddRow(grid, "Attempts", _attempts);
        AddWide(grid, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(LogicalToDeviceUnits(590), 0),
            Margin = new Padding(3, 6, 3, 6),
            Text = "Bytes in hex, e.g. AA EC 09 00 01 01 A1. In the replies ?? matches any byte. " +
                   "Without the state replies a command counts as done once it is sent.",
        });

        _testButtons =
        [
            NewButton("Test ON", (_, _) => RunTest(d => Describe(d.SetGameMode(true), true, d.CanVerify))),
            NewButton("Test OFF", (_, _) => RunTest(d => Describe(d.SetGameMode(false), false, d.CanVerify))),
            NewButton("Read state", (_, _) => RunTest(d => d.ReadState() switch
            {
                true => "State: ON",
                false => "State: OFF",
                null => "State: unknown, no reply matched the state replies.",
            })),
        ];
        var tests = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        tests.Controls.AddRange(_testButtons);
        AddWide(grid, tests);
        AddWide(grid, _output, fill: true);
        var guide = new LinkLabel { Text = "How to find the commands of your headphones", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        guide.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true });
        AddWide(grid, guide);

        var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        left.Controls.Add(NewButton("Import...", (_, _) => Import()));
        left.Controls.Add(NewButton("Export...", (_, _) => Export()));
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = NewButton("Cancel", (_, _) => Close());
        right.Controls.Add(cancel);
        right.Controls.Add(NewButton("Save", (_, _) => Save()));
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, AutoSize = true, Padding = new Padding(10, 0, 10, 10) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.Controls.Add(left, 0, 0);
        bottom.Controls.Add(right, 1, 0);
        CancelButton = cancel;

        // Docking is laid out from the last added control: the bottom bar first, then the grid fills the rest
        Controls.Add(grid);
        Controls.Add(bottom);

        Fill(profile);
        SelectPreset(profile);
        // Editing any field of a preset turns the selection into "Custom"
        foreach (var box in new[] { _name, _on, _off, _query, _onReply, _offReply }) box.TextChanged += (_, _) => OnFieldEdited();
        _device.TextChanged += (_, _) => OnFieldEdited();
        foreach (var number in new[] { _channel, _delay, _attempts }) number.ValueChanged += (_, _) => OnFieldEdited();
        Load += async (_, _) => await LoadDevicesAsync();
    }

    void Fill(Profile p)
    {
        _filling = true;
        try
        {
            _name.Text = p.Name;
            _device.Text = p.DeviceName;
            _channel.Value = Math.Clamp(p.Channel, 1, 30);
            _on.Text = p.OnCommand;
            _off.Text = p.OffCommand;
            _query.Text = p.StateQuery;
            _onReply.Text = p.StateOnReply;
            _offReply.Text = p.StateOffReply;
            _delay.Value = Math.Clamp(p.ConnectDelayMs, 0, 30000);
            _attempts.Value = Math.Clamp(p.Attempts, 1, 10);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>Selects the preset identical to the profile, or "Custom".</summary>
    void SelectPreset(Profile p)
    {
        _filling = true;
        _preset.SelectedItem = Profile.Presets.FirstOrDefault(preset => preset.ToJson() == p.ToJson()) ?? (object)Custom;
        _filling = false;
    }

    void OnFieldEdited()
    {
        if (_filling || _preset.SelectedItem is not Profile) return;
        _filling = true;
        _preset.SelectedItem = Custom;
        _filling = false;
    }

    /// <summary>Profile from the form fields; throws FormatException naming the wrong field.</summary>
    Profile ReadForm()
    {
        var p = new Profile
        {
            Name = _name.Text.Trim(),
            DeviceName = _device.Text.Trim(),
            Channel = (int)_channel.Value,
            OnCommand = _on.Text.Trim(),
            OffCommand = _off.Text.Trim(),
            StateQuery = _query.Text.Trim(),
            StateOnReply = _onReply.Text.Trim(),
            StateOffReply = _offReply.Text.Trim(),
            ConnectDelayMs = (int)_delay.Value,
            Attempts = (int)_attempts.Value,
        };
        if (p.DeviceName.Length == 0) throw new FormatException("Choose the Bluetooth device.");
        if (p.OnCommand.Length == 0) throw new FormatException("The ON command is required.");
        if (p.Name.Length == 0) p.Name = p.DeviceName;
        Check("ON command", () => Hex.Parse(p.OnCommand));
        Check("OFF command", () => Hex.Parse(p.OffCommand));
        Check("State query", () => Hex.Parse(p.StateQuery));
        Check("State reply when ON", () => Hex.ParsePattern(p.StateOnReply));
        Check("State reply when OFF", () => Hex.ParsePattern(p.StateOffReply));
        return p;
    }

    static void Check(string field, Action parse)
    {
        try { parse(); }
        catch (FormatException ex) { throw new FormatException($"{field}: {ex.Message}"); }
    }

    async Task LoadDevicesAsync()
    {
        try
        {
            var names = (await PairedDevices.ListAsync()).Select(d => d.Name).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (IsDisposed) return;
            var current = _device.Text;
            _filling = true;
            _device.Items.AddRange(names);
            _device.Text = current;
            _filling = false;
        }
        catch (Exception ex)
        {
            Append("Could not list the paired devices: " + ex.Message);
        }
    }

    async void RunTest(Func<GameModeDevice, string> test)
    {
        Profile p;
        try { p = ReadForm(); }
        catch (FormatException ex) { Append(ex.Message); return; }

        foreach (var b in _testButtons) b.Enabled = false;
        try
        {
            var device = await PairedDevices.FindAsync(p.DeviceName);
            if (device is null)
            {
                Append($"Paired device \"{p.DeviceName}\" not found.");
                return;
            }
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                Append($"{device.Name} does not look connected, trying anyway...");
            var game = new GameModeDevice(p, device.BluetoothAddress, line => BeginInvoke(new Action(() => Append(line))));
            Append(await Task.Run(() => test(game)));
        }
        catch (Exception ex)
        {
            Append("Error: " + ex.Message);
        }
        finally
        {
            if (!IsDisposed) foreach (var b in _testButtons) b.Enabled = true;
        }
    }

    static string Describe(bool? state, bool wanted, bool canVerify) => state switch
    {
        _ when state == wanted => $"Game mode {(wanted ? "ON" : "OFF")}, confirmed by the headphones.",
        null when !canVerify => "Command sent. Fill in the state replies to have it confirmed.",
        null => "Command sent, but no reply matched the state replies.",
        _ => $"The headphones report game mode {(state.Value ? "ON" : "OFF")}.",
    };

    void Save()
    {
        try { Result = ReadForm(); }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    void Import()
    {
        using var dialog = new OpenFileDialog { Filter = JsonFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = Profile.FromJson(File.ReadAllText(dialog.FileName));
            Fill(imported);
            SelectPreset(imported);
            Append("Imported " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Export()
    {
        Profile p;
        try { p = ReadForm(); }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var slug = new string(p.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        using var dialog = new SaveFileDialog { Filter = JsonFilter, FileName = slug + ".json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, p.ToJson());
        Append("Exported " + dialog.FileName);
    }

    void Append(string line) => _output.AppendText(line + Environment.NewLine);

    static TextBox NewBox(bool hex) => new()
    {
        Dock = DockStyle.Fill,
        Font = hex ? new Font("Consolas", 9.75f) : SystemFonts.MessageBoxFont,
    };

    static NumericUpDown NewNumber(int min, int max, int increment) => new()
    {
        Minimum = min, Maximum = max, Increment = increment, Anchor = AnchorStyles.Left,
    };

    static Button NewButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += onClick;
        return button;
    }

    static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    static void AddWide(TableLayoutPanel grid, Control control, bool fill = false)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
    }
}
