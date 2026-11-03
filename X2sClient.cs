using System.Net;
using System.Net.Sockets;

namespace EdifierGameMode;

/// <summary>
/// Commands to the Edifier X2s over RFCOMM. Protocol reverse-engineered from the
/// Bluetooth HCI log of 2026-11-03 (same as ..\edifier_x2s.py):
///   AA EC &lt;cmd&gt; &lt;data length, 2 bytes big-endian&gt; &lt;data&gt; &lt;checksum&gt;
///   checksum = sum of all preceding bytes, keeping the lowest byte
///   AA = PC -> earbuds, BB = earbuds -> PC
///   cmd 0x09 = set game mode (01 ON, 00 OFF), cmd 0x08 = read state
/// </summary>
sealed class X2sClient(ulong address, int channel = 2)
{
    const byte CmdGetGame = 0x08;
    const byte CmdSetGame = 0x09;

    /// <summary>Sets the game mode and returns the state read back from the earbuds.</summary>
    public bool SetGameMode(bool on)
    {
        using var socket = Connect();
        Exchange(socket, Packet(CmdSetGame, on ? (byte)1 : (byte)0));
        Thread.Sleep(200);
        return ReadState(socket);
    }

    public bool GetGameMode()
    {
        using var socket = Connect();
        return ReadState(socket);
    }

    public static byte[] Packet(byte cmd, params byte[] data)
    {
        var p = new byte[5 + data.Length + 1];
        p[0] = 0xAA; p[1] = 0xEC; p[2] = cmd;
        p[3] = (byte)(data.Length >> 8); p[4] = (byte)data.Length;
        data.CopyTo(p, 5);
        p[^1] = Checksum(p.AsSpan(0, p.Length - 1));
        return p;
    }

    static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)sum;
    }

    static bool ReadState(Socket socket)
    {
        foreach (var f in Exchange(socket, Packet(CmdGetGame)))
            if (f[2] == CmdGetGame && f.Length > 6) return f[5] != 0;
        throw new IOException("no reply to the state query");
    }

    Socket Connect()
    {
        var socket = new Socket(BluetoothEndPoint.Family, SocketType.Stream, BluetoothEndPoint.Rfcomm);
        try
        {
            socket.Connect(new BluetoothEndPoint(address, channel));
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
    /// Sends a packet and collects BB EC replies until one with the same command
    /// code arrives, or until the timeout.
    /// </summary>
    static List<byte[]> Exchange(Socket socket, byte[] packet, int timeoutMs = 1500)
    {
        Log.Write("-> " + Convert.ToHexString(packet));
        socket.Send(packet);

        var buf = new List<byte>();
        var chunk = new byte[1024];
        var frames = new List<byte[]>();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                int n = socket.Receive(chunk);
                if (n == 0) break;
                buf.AddRange(chunk.Take(n));
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            frames = ParseFrames(buf);
            if (frames.Any(f => f[2] == packet[2])) break;
        }
        foreach (var f in frames) Log.Write("<- " + Convert.ToHexString(f));
        return frames;
    }

    static List<byte[]> ParseFrames(List<byte> buf)
    {
        var frames = new List<byte[]>();
        int i = 0;
        while (true)
        {
            int start = -1;
            for (int j = i; j + 1 < buf.Count; j++)
                if (buf[j] == 0xBB && buf[j + 1] == 0xEC) { start = j; break; }
            if (start < 0 || buf.Count - start < 6) break;

            int end = start + 5 + (buf[start + 3] << 8 | buf[start + 4]) + 1;
            if (buf.Count < end) break;
            var f = buf.GetRange(start, end - start).ToArray();
            if (Checksum(f.AsSpan(0, f.Length - 1)) == f[^1]) frames.Add(f);
            i = end;
        }
        return frames;
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
