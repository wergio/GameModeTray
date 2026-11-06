using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Storage.Streams;

namespace GameModeTray;

/// <summary>How the companion app can talk to a pair of headphones.</summary>
enum DataLink { Unknown, Rfcomm, BleGatt }

/// <summary>A paired Bluetooth device with what the detection wizard needs to know about it.</summary>
sealed record ScannedDevice(
    BluetoothDevice Device, string Name, ulong Address, bool Connected, bool IsAudio,
    Profile? Preset, DataLink Link, IReadOnlyList<int> Channels)
{
    public string LinkText => Link switch
    {
        DataLink.Rfcomm when Channels.Count > 0 => $"RFCOMM (SPP), ch. {string.Join(", ", Channels)}",
        DataLink.Rfcomm => "RFCOMM (SPP)",
        DataLink.BleGatt => "BLE GATT",
        _ => "Unknown",
    };

    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Select(i => ((byte)(Address >> (8 * (5 - i)))).ToString("X2")));
}

/// <summary>Lists the paired devices and works out which link their companion app can use.</summary>
static class DeviceScanner
{
    const string SigUuidSuffix = "-0000-1000-8000-00805f9b34fb";
    const uint SerialPort = 0x1101;

    public static async Task<List<ScannedDevice>> ScanAsync()
    {
        var infos = await PairedDevices.ListAsync();
        var scans = await Task.WhenAll(infos.Select(i => ScanOneAsync(i.Id)));
        return scans.OfType<ScannedDevice>().OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<ScannedDevice?> ScanOneAsync(string id)
    {
        var device = await BluetoothDevice.FromIdAsync(id);
        if (device is null) return null;
        bool connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        var preset = Profile.Presets.FirstOrDefault(p => p.DeviceName.Length > 0 &&
            device.Name.StartsWith(p.DeviceName, StringComparison.OrdinalIgnoreCase));
        var (link, channels) = await ReadLinkAsync(device, connected);
        return new ScannedDevice(device, device.Name, device.BluetoothAddress, connected,
            device.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo, preset, link, channels);
    }

    /// <summary>
    /// Windows only sees the RFCOMM services of the headphones. A data channel means RFCOMM; if
    /// fresh data from connected headphones shows none, the app must be using BLE GATT.
    /// </summary>
    static async Task<(DataLink, IReadOnlyList<int>)> ReadLinkAsync(BluetoothDevice device, bool connected)
    {
        try
        {
            var result = await device.GetRfcommServicesAsync(connected ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached);
            bool fresh = connected && result.Error == BluetoothError.Success;
            if (!fresh) result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
            if (result.Error != BluetoothError.Success) return (DataLink.Unknown, []);

            var data = result.Services.Where(IsDataService).ToList();
            if (data.Count > 0)
            {
                var channels = new List<int>();
                foreach (var service in data)
                    if (await ReadChannelAsync(service) is int channel && !channels.Contains(channel)) channels.Add(channel);
                channels.Sort();
                return (DataLink.Rfcomm, channels);
            }
            // No services at all says nothing: every pair of headphones has at least the hands-free one
            return (fresh && result.Services.Count > 0 ? DataLink.BleGatt : DataLink.Unknown, []);
        }
        catch (Exception ex)
        {
            Log.Write($"RFCOMM services of {device.Name}: {ex.Message}");
            return (DataLink.Unknown, []);
        }
    }

    /// <summary>Serial Port or a vendor UUID; hands-free, phone book and the like use other standard ones.</summary>
    static bool IsDataService(RfcommDeviceService service)
    {
        var uuid = service.ServiceId.Uuid.ToString("D");
        return !uuid.EndsWith(SigUuidSuffix, StringComparison.OrdinalIgnoreCase) ||
               Convert.ToUInt32(uuid[..8], 16) == SerialPort;
    }

    /// <summary>The server channel from the SDP protocol descriptor list (UUID16 0x0003, then a uint8).</summary>
    static async Task<int?> ReadChannelAsync(RfcommDeviceService service)
    {
        var attributes = await service.GetSdpRawAttributesAsync(BluetoothCacheMode.Cached);
        if (!attributes.TryGetValue(4, out var buffer)) return null;
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        for (int i = 0; i + 4 < bytes.Length; i++)
            if (bytes[i] == 0x19 && bytes[i + 1] == 0x00 && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x08)
                return bytes[i + 4];
        return null;
    }
}
