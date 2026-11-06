using Windows.Devices.Bluetooth;

namespace GameModeTray;

/// <summary>The paired Bluetooth devices, with a button to start the detection on the eligible ones.</summary>
sealed class DeviceListForm : Form
{
    const int ActionColumn = 4;

    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, BackgroundColor = SystemColors.Window,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
    };
    readonly CheckBox _showAll = new() { Text = "Show non-headset devices", AutoSize = true, Margin = new Padding(3, 7, 12, 3) };
    readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
    readonly Windows.Foundation.TypedEventHandler<BluetoothDevice, object> _connectionChanged;
    readonly Icon _icon;
    List<ScannedDevice> _devices = [];
    bool _scanning;

    public Profile? Result { get; private set; }

    public DeviceListForm(Icon icon)
    {
        _icon = icon;
        Text = "GameModeTray - Detect commands";
        Icon = icon;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(380));
        MinimumSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(260));

        _grid.Columns.Add("name", "Headphones");
        _grid.Columns.Add("status", "Status");
        _grid.Columns.Add("support", "Support");
        _grid.Columns.Add("link", "Connection type");
        _grid.Columns.Add("action", "");
        _grid.Columns[0].FillWeight = 140;
        _grid.Columns[1].FillWeight = 75;
        _grid.Columns[2].FillWeight = 90;
        _grid.Columns[3].FillWeight = 135;
        _grid.Columns[4].FillWeight = 165;
        foreach (DataGridViewColumn column in _grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.CellContentClick += OnCellClick;

        var intro = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 10, 10, 8),
            MaximumSize = new Size(LogicalToDeviceUnits(840), 0),
            Text = "Pick the headphones to detect the commands of. They must be paired with this PC and connected, " +
                   "and their companion app must use RFCOMM.",
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += async (_, _) => await ScanAsync();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        CancelButton = close;
        _showAll.CheckedChanged += (_, _) => FillGrid();

        var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        left.Controls.AddRange([_showAll, _status]);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        right.Controls.AddRange([close, refresh]);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, AutoSize = true, Padding = new Padding(10, 6, 10, 10) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        bottom.Controls.Add(left, 0, 0);
        bottom.Controls.Add(right, 1, 0);

        var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        center.Controls.Add(_grid);
        Controls.Add(center);
        Controls.Add(bottom);
        Controls.Add(intro);

        // The event is raised on a thread-pool thread
        _connectionChanged = (_, _) => { if (IsHandleCreated) BeginInvoke(new Action(async () => await ScanAsync())); };
        Load += async (_, _) => await ScanAsync();
        FormClosed += (_, _) => Release();
    }

    async Task ScanAsync()
    {
        if (_scanning || IsDisposed) return;
        _scanning = true;
        _status.Text = "Reading the paired devices...";
        try
        {
            var devices = await DeviceScanner.ScanAsync();
            if (IsDisposed) { foreach (var d in devices) d.Device.Dispose(); return; }
            Release();
            _devices = devices;
            foreach (var d in _devices) d.Device.ConnectionStatusChanged += _connectionChanged;
            FillGrid();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not list the paired devices: " + ex.Message;
        }
        finally
        {
            _scanning = false;
        }
    }

    void Release()
    {
        foreach (var d in _devices)
        {
            d.Device.ConnectionStatusChanged -= _connectionChanged;
            d.Device.Dispose();
        }
        _devices = [];
    }

    void FillGrid()
    {
        var shown = _devices.Where(d => _showAll.Checked || d.IsAudio).ToList();
        _grid.Rows.Clear();
        foreach (var d in shown)
        {
            int row = _grid.Rows.Add(d.Name, d.Connected ? "Connected" : "Not connected",
                d.Preset is null ? "Needs detection" : "Present", d.LinkText, ActionText(d));
            _grid.Rows[row].Tag = d;
            _grid.Rows[row].Cells[0].ToolTipText = d.AddressText;
            if (CanStart(d)) _grid.Rows[row].Cells[ActionColumn] = new DataGridViewButtonCell { Value = "Start detection procedure", UseColumnTextForButtonValue = false };
        }
        _grid.ClearSelection();
        _status.Text = shown.Count == 0 ? "No paired headphones found." : "";
    }

    static bool CanStart(ScannedDevice d) =>
        d.Connected && (Program.DetectAll || (d.Preset is null && d.Link != DataLink.BleGatt));

    static string ActionText(ScannedDevice d)
    {
        if (CanStart(d)) return "";
        if (d.Link == DataLink.BleGatt) return "BLE GATT not supported";
        if (d.Preset is not null && !Program.DetectAll) return "Already supported";
        return "Connect them to start";
    }

    void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != ActionColumn) return;
        if (_grid.Rows[e.RowIndex].Cells[ActionColumn] is not DataGridViewButtonCell) return;
        if (_grid.Rows[e.RowIndex].Tag is not ScannedDevice device) return;

        // The headphones move between phone and PC during the wizard, and a scan would dispose
        // the device it is using: no scans until it closes
        _scanning = true;
        using var wizard = new DetectWizardForm(device, _icon);
        var answer = wizard.ShowDialog(this);
        _scanning = false;
        if (answer != DialogResult.OK)
        {
            _ = ScanAsync();
            return;
        }
        Result = wizard.Result;
        DialogResult = DialogResult.OK;
        Close();
    }
}
