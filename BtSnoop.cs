using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GameModeTray;

/// <summary>Data carried by one RFCOMM frame. Sent means phone -> headphones.</summary>
sealed record RfcommMessage(long TimeUs, bool Sent, ulong Peer, int Channel, byte[] Data);

/// <summary>
/// Reads an Android Bluetooth HCI snoop log (or the bugreport zip holding it) down to the
/// RFCOMM data: H4 packets -> ACL reassembly -> L2CAP -> RFCOMM.
/// </summary>
sealed class BtSnoopLog
{
    const int DatalinkH4 = 1002;
    const int RecordHeader = 24;
    const byte H4Acl = 2, H4Event = 4;
    const int CidSignaling = 1, CidAtt = 4, PsmRfcomm = 3;

    readonly Dictionary<int, ulong> _peers = [];                       // ACL handle -> Bluetooth address
    readonly Dictionary<(int Handle, bool Sent), List<byte>> _partial = [];
    readonly Dictionary<(int Handle, int SourceCid), bool> _pending = []; // open requests -> sent by the phone
    readonly HashSet<(int Handle, bool Sent, int Cid)> _rfcomm = [];

    public List<RfcommMessage> Messages { get; } = [];
    public int TruncatedPackets { get; private set; }
    public int AttWrites { get; private set; }

    /// <summary>Addresses of the peers that connected during the log.</summary>
    public IReadOnlyCollection<ulong> Peers => _peers.Values;

    public static BtSnoopLog Load(string path)
    {
        var log = new BtSnoopLog();
        log.Parse(ReadLogBytes(path));
        return log;
    }

    static byte[] ReadLogBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 2 || bytes[0] != 'P' || bytes[1] != 'K') return bytes;

        // The path inside a bugreport changes between manufacturers: look for the name
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var logs = zip.Entries.Where(e => e.Name.StartsWith("btsnoop", StringComparison.OrdinalIgnoreCase) && e.Length > 0).ToList();
        var entry = logs.FirstOrDefault(e => e.Name.Equals("btsnoop_hci.log", StringComparison.OrdinalIgnoreCase))
                    ?? logs.MaxBy(e => e.Length)
                    ?? throw new FormatException("There is no btsnoop log in this zip. Check that the Bluetooth HCI snoop log is set to Enabled and that Bluetooth was restarted afterwards.");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    void Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 16 || Encoding.ASCII.GetString(file[..7]) != "btsnoop")
            throw new FormatException("This is not a btsnoop log.");
        int datalink = BinaryPrimitives.ReadInt32BigEndian(file[12..]);
        if (datalink != DatalinkH4) throw new FormatException($"Unsupported btsnoop datalink {datalink}.");

        int position = 16;
        while (position + RecordHeader <= file.Length)
        {
            int original = BinaryPrimitives.ReadInt32BigEndian(file[position..]);
            int included = BinaryPrimitives.ReadInt32BigEndian(file[(position + 4)..]);
            int flags = BinaryPrimitives.ReadInt32BigEndian(file[(position + 8)..]);
            long time = BinaryPrimitives.ReadInt64BigEndian(file[(position + 16)..]);
            position += RecordHeader;
            if (included < 0 || position + included > file.Length) break;   // log cut short

            var packet = file.Slice(position, included);
            position += included;
            if (packet.Length == 0) continue;
            if (included < original) { TruncatedPackets++; continue; }

            bool sent = (flags & 1) == 0;
            if (packet[0] == H4Event) OnEvent(packet[1..]);
            else if (packet[0] == H4Acl) OnAcl(packet[1..], sent, time);
        }
    }

    void OnEvent(ReadOnlySpan<byte> e)
    {
        if (e.Length < 2) return;
        var data = e[2..];
        switch (e[0])
        {
            case 0x03 when data.Length >= 9 && data[0] == 0:                  // Connection Complete
                OnConnected(BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), data.Slice(3, 6));
                break;
            case 0x3E when data.Length >= 12 && data[0] is 0x01 or 0x0A && data[1] == 0:  // LE Connection Complete
                OnConnected(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), data.Slice(6, 6));
                break;
        }
    }

    void OnConnected(int handle, ReadOnlySpan<byte> address)
    {
        ulong value = 0;
        for (int i = 5; i >= 0; i--) value = (value << 8) | address[i];
        _peers[handle & 0x0FFF] = value;
        _rfcomm.RemoveWhere(c => c.Handle == (handle & 0x0FFF));   // handles get reused
    }

    void OnAcl(ReadOnlySpan<byte> acl, bool sent, long time)
    {
        if (acl.Length < 4) return;
        int header = BinaryPrimitives.ReadUInt16LittleEndian(acl);
        int handle = header & 0x0FFF;
        bool continuation = ((header >> 12) & 3) == 1;
        var key = (handle, sent);
        if (!continuation) _partial[key] = [];
        if (!_partial.TryGetValue(key, out var buffer)) return;
        buffer.AddRange(acl[4..]);

        if (buffer.Count < 4) return;
        var frame = buffer.ToArray().AsSpan();
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        if (frame.Length < 4 + length) return;
        _partial.Remove(key);

        int cid = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
        var payload = frame.Slice(4, length);
        if (cid == CidSignaling) OnSignaling(handle, sent, payload);
        else if (cid == CidAtt) { if (sent && payload.Length > 0 && payload[0] is 0x12 or 0x52) AttWrites++; }
        else if (_rfcomm.Contains((handle, sent, cid))) OnRfcomm(payload, sent, handle, time);
    }

    void OnSignaling(int handle, bool sent, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 4)
        {
            byte code = data[0];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            if (data.Length < 4 + length) return;
            var command = data.Slice(4, length);
            data = data[(4 + length)..];

            if (code == 0x02 && command.Length >= 4)         // Connection Request
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(command) == PsmRfcomm)
                    _pending[(handle, BinaryPrimitives.ReadUInt16LittleEndian(command[2..]))] = sent;
            }
            else if (code == 0x03 && command.Length >= 8)    // Connection Response
            {
                int destination = BinaryPrimitives.ReadUInt16LittleEndian(command);
                int source = BinaryPrimitives.ReadUInt16LittleEndian(command[2..]);
                int result = BinaryPrimitives.ReadUInt16LittleEndian(command[4..]);
                if (result != 0 || !_pending.Remove((handle, source), out bool phoneAsked)) continue;
                // A frame carries the CID of its receiver: "source" belongs to who asked
                _rfcomm.Add((handle, true, phoneAsked ? destination : source));
                _rfcomm.Add((handle, false, phoneAsked ? source : destination));
            }
        }
    }

    void OnRfcomm(ReadOnlySpan<byte> frame, bool sent, int handle, long time)
    {
        if (frame.Length < 4) return;
        int dlci = frame[0] >> 2;
        byte control = frame[1];
        if (dlci == 0 || (control & 0xEF) != 0xEF) return;     // multiplexer control, or not UIH

        int position = 3;
        int length = frame[2] >> 1;
        if ((frame[2] & 1) == 0)
        {
            length |= frame[3] << 7;
            position = 4;
        }
        if (control == 0xFF) position++;                       // credit byte
        if (length == 0 || position + length > frame.Length) return;

        _peers.TryGetValue(handle, out ulong peer);
        Messages.Add(new RfcommMessage(time, sent, peer, dlci >> 1, frame.Slice(position, length).ToArray()));
    }
}
