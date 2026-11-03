using System.Net;
using System.Net.Sockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace GameModeTray;

/// <summary>
/// Sends the byte strings of a <see cref="Profile"/> to the headphones over RFCOMM
/// and recognises the state replies.
/// </summary>
sealed class GameModeDevice
{
    readonly ulong _address;
    readonly int _channel;
    readonly Action<string> _trace;
    readonly byte[] _on;
    readonly byte[]? _off, _query;
    readonly int[]? _onReply, _offReply;

    public GameModeDevice(Profile profile, ulong address, Action<string> trace)
    {
        if (!string.Equals(profile.Transport, "rfcomm", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"transport \"{profile.Transport}\" is not supported, only rfcomm");

        _address = address;
        _channel = profile.Channel;
        _trace = trace;
        _on = Hex.Parse(profile.OnCommand);
        if (_on.Length == 0) throw new FormatException("the ON command is empty");
        _off = OrNull(Hex.Parse(profile.OffCommand));
        _query = OrNull(Hex.Parse(profile.StateQuery));
        _onReply = OrNull(Hex.ParsePattern(profile.StateOnReply));
        _offReply = OrNull(Hex.ParsePattern(profile.StateOffReply));
    }

    /// <summary>True when the profile describes the state replies, so a command can be confirmed.</summary>
    public bool CanVerify => _onReply is not null || _offReply is not null;

    /// <summary>True when the profile can ask the headphones for their current state.</summary>
    public bool CanReadState => _query is not null && CanVerify;

    /// <summary>
    /// Sends the ON/OFF command and returns the state the headphones report, or null
    /// when no reply matched (always null if the profile has no state replies).
    /// </summary>
    public bool? SetGameMode(bool on)
    {
        var command = on ? _on : _off ?? throw new InvalidOperationException("no OFF command configured");
        using var socket = Connect();
        // Some headphones report the new state on their own, otherwise ask for it
        var state = MatchState(Exchange(socket, command, CanVerify ? 1000 : 500));
        if (state is null && CanVerify && _query is not null)
        {
            Thread.Sleep(200);
            state = MatchState(Exchange(socket, _query, 1500));
        }
        return state;
    }

    public bool? ReadState()
    {
        if (_query is null) throw new InvalidOperationException("no state query configured");
        using var socket = Connect();
        return MatchState(Exchange(socket, _query, 1500));
    }

    static T[]? OrNull<T>(T[] array) => array.Length == 0 ? null : array;

    /// <summary>The state shown by the most recent matching reply, or null.</summary>
    bool? MatchState(IReadOnlyList<byte> data)
    {
        int on = _onReply is null ? -1 : Hex.LastIndexOf(data, _onReply);
        int off = _offReply is null ? -1 : Hex.LastIndexOf(data, _offReply);
        if (on < 0 && off < 0) return null;
        return on > off;
    }

    Socket Connect()
    {
        var socket = new Socket(BluetoothEndPoint.Family, SocketType.Stream, BluetoothEndPoint.Rfcomm);
        try
        {
            socket.Connect(new BluetoothEndPoint(_address, _channel));
            socket.ReceiveTimeout = 300;
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a packet and collects what comes back until a state reply is recognised
    /// or the timeout expires.
    /// </summary>
    List<byte> Exchange(Socket socket, byte[] packet, int timeoutMs)
    {
        _trace("-> " + Hex.Format(packet));
        socket.Send(packet);

        var received = new List<byte>();
        var chunk = new byte[1024];
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                int n = socket.Receive(chunk);
                if (n == 0) break;
                received.AddRange(chunk.Take(n));
                _trace("<- " + Hex.Format(chunk.Take(n)));
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            if (CanVerify && MatchState(received) is not null) break;
        }
        return received;
    }
}

static class PairedDevices
{
    public static async Task<IReadOnlyList<DeviceInformation>> ListAsync() =>
        await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));

    /// <summary>The paired classic Bluetooth device with this name (exact, then prefix), or null.</summary>
    public static async Task<BluetoothDevice?> FindAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var devices = await ListAsync();
        var info = devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? devices.FirstOrDefault(d => d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        return info is null ? null : await BluetoothDevice.FromIdAsync(info.Id);
    }

    /// <summary>The first preset whose headphones are paired with this PC, or null.</summary>
    public static async Task<Profile?> FindPresetAsync(IEnumerable<Profile> presets)
    {
        var names = (await ListAsync()).Select(d => d.Name).ToList();
        return presets.FirstOrDefault(p => p.DeviceName.Length > 0 &&
            names.Any(n => n.StartsWith(p.DeviceName, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Winsock Bluetooth address (SOCKADDR_BTH) for an RFCOMM socket.</summary>
sealed class BluetoothEndPoint(ulong address, int port) : EndPoint
{
    public const AddressFamily Family = (AddressFamily)32;   // AF_BTH
    public const ProtocolType Rfcomm = (ProtocolType)3;      // BTHPROTO_RFCOMM

    public override AddressFamily AddressFamily => Family;

    // SOCKADDR_BTH (ws2bth.h, packed): family(2) btAddr(8) serviceClassId(16) port(4)
    public override SocketAddress Serialize()
    {
        var sa = new SocketAddress(Family, 30);
        for (int i = 0; i < 8; i++) sa[2 + i] = (byte)(address >> (8 * i));
        for (int i = 0; i < 4; i++) sa[26 + i] = (byte)(port >> (8 * i));
        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress) => this;
}
