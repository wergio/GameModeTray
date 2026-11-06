namespace GameModeTray;

/// <summary>Two commands that alternate in the log, packets exactly as the companion app sent them.</summary>
sealed record CommandPair(int Channel, byte[] On, byte[] Off, int Flips, int Score);

/// <summary>What was found in a log. Problem explains why there is nothing to try.</summary>
sealed record FinderResult(List<CommandPair> Pairs, List<byte[]> Queries, string? Problem, bool AddressFound, int MessageCount);

/// <summary>
/// Finds the game mode commands in the traffic recorded while following the wizard's script:
/// app opened with game mode off, ON/OFF toggled a few times, left ON, app opened again.
/// </summary>
static class CommandFinder
{
    const int MaxPairs = 5, MaxQueries = 16, MinFlips = 2, MaxCounterStep = 32, MaxReplyDifferences = 3;
    const long MinRegularGapUs = 1_500_000, MaxRegularGapUs = 20_000_000;

    public static FinderResult Analyze(BtSnoopLog log, ulong address)
    {
        var messages = log.Messages.Where(m => m.Peer == address).ToList();
        bool addressFound = messages.Count > 0;
        if (!addressFound) messages = log.Messages;   // e.g. the log began after the connection

        var channels = messages.GroupBy(m => m.Channel).Where(g => !IsTextChannel(g)).ToList();
        int count = channels.Sum(g => g.Count());
        if (count == 0) return new FinderResult([], [], Explain(log), addressFound, 0);

        var pairs = channels.SelectMany(g => FindPairs(g.ToList())).OrderByDescending(p => p.Score).Take(MaxPairs).ToList();
        if (pairs.Count == 0)
        {
            return new FinderResult([], [], "No two commands alternate in this log. Record it again, changing game mode " +
                "at least 3 times in the companion app and nothing else.", addressFound, count);
        }
        var queries = FindQueries(messages.Where(m => m.Channel == pairs[0].Channel).ToList(), pairs[0]);
        return new FinderResult(pairs, queries, null, addressFound, count);
    }

    static string Explain(BtSnoopLog log)
    {
        if (log.TruncatedPackets > 0)
            return "The payloads are missing from this log: set the Bluetooth HCI snoop log to Enabled, not Filtered, and record it again.";
        if (log.AttWrites > 0)
            return "There is no RFCOMM data in this log, but there are BLE GATT writes: these headphones seem to use BLE GATT, which is not supported yet.";
        return "There is no RFCOMM data in this log. Restart Bluetooth on the phone before connecting the headphones, " +
               "so that the log contains the whole connection.";
    }

    /// <summary>The hands-free channel carries AT commands in plain text.</summary>
    static bool IsTextChannel(IEnumerable<RfcommMessage> messages)
    {
        var list = messages.ToList();
        return list.Count(m => m.Data.All(b => b is >= 0x20 and <= 0x7E or 0x0D or 0x0A)) * 5 >= list.Count * 4;
    }

    static IEnumerable<CommandPair> FindPairs(List<RfcommMessage> channel)
    {
        var sent = channel.Where(m => m.Sent);
        foreach (var sameLength in sent.GroupBy(m => m.Data.Length))
        {
            var list = sameLength.ToList();
            if (list.Count < MinFlips + 1) continue;
            var counters = FindCounters(list);
            // Without a checksum the first pass is enough; with a sequence number the checksum changes too
            for (int trailing = 0; trailing <= 2; trailing++)
            {
                var ignored = new HashSet<int>(counters);
                for (int i = 1; i <= trailing; i++) ignored.Add(sameLength.Key - i);
                var found = FindPairsIn(list, ignored).ToList();
                if (found.Count == 0) continue;
                foreach (var pair in found) yield return pair;
                break;
            }
        }
    }

    /// <summary>Positions holding a sequence number: they grow by a small step at every message.</summary>
    static List<int> FindCounters(List<RfcommMessage> list)
    {
        var counters = new List<int>();
        if (list.Count < 4) return counters;
        for (int p = 0; p < list[0].Data.Length; p++)
        {
            int growing = 0;
            for (int i = 1; i < list.Count; i++)
                if (((list[i].Data[p] - list[i - 1].Data[p]) & 0xFF) is >= 1 and <= MaxCounterStep) growing++;
            if (growing * 5 >= (list.Count - 1) * 4 && list.Select(m => m.Data[p]).Distinct().Count() * 2 > list.Count)
                counters.Add(p);
        }
        return counters;
    }

    static IEnumerable<CommandPair> FindPairsIn(List<RfcommMessage> list, HashSet<int> ignored)
    {
        string Key(RfcommMessage m) => string.Concat(m.Data.Select((b, i) => ignored.Contains(i) ? "??" : b.ToString("X2")));

        var groups = list.GroupBy(Key).Where(g => g.Count() >= MinFlips).ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            for (int j = i + 1; j < groups.Count; j++)
            {
                var a = groups[i].First().Data;
                var b = groups[j].First().Data;
                int different = Enumerable.Range(0, a.Length).Count(p => !ignored.Contains(p) && a[p] != b[p]);
                if (different is < 1 or > 2) continue;

                string keyA = groups[i].Key;
                var sequence = list.Where(m => { var k = Key(m); return k == keyA || k == groups[j].Key; }).OrderBy(m => m.TimeUs).ToList();
                var flips = new List<long>();
                for (int k = 1; k < sequence.Count; k++)
                    if (Key(sequence[k]) != Key(sequence[k - 1])) flips.Add(sequence[k].TimeUs - sequence[k - 1].TimeUs);
                if (flips.Count < MinFlips) continue;

                flips.Sort();
                long medianGap = flips[flips.Count / 2];
                bool regular = medianGap is >= MinRegularGapUs and <= MaxRegularGapUs;
                int repeats = sequence.Count - 1 - flips.Count;
                int score = flips.Count * 2 - repeats + (regular ? 6 : 0);

                // The script ends with game mode on, so the last command is ON
                var last = sequence[^1];
                string lastKey = Key(last);
                var other = sequence.Last(m => Key(m) != lastKey);
                yield return new CommandPair(last.Channel, last.Data, other.Data, flips.Count, score);
            }
        }
    }

    /// <summary>
    /// What the app sends more than once, apart from the toggles: the state query is among these,
    /// as the app asks for the state every time it opens. Its reply cannot be told from the log,
    /// as the app sends its requests in bursts and the answers do not follow them one by one, so
    /// the headphones decide when the candidates are tried.
    /// </summary>
    static List<byte[]> FindQueries(List<RfcommMessage> channel, CommandPair pair)
    {
        static bool Same(byte[] a, byte[] b, int tolerance) => Differences(a, b) >= 0 && Differences(a, b) <= tolerance;

        var sent = channel.Where(m => m.Sent && !Same(m.Data, pair.On, 2) && !Same(m.Data, pair.Off, 2)).Select(m => m.Data).ToList();
        var queries = new List<byte[]>();
        foreach (var packet in sent)
        {
            // Tolerance 1: a sequence number makes every copy different
            if (!queries.Any(q => Same(q, packet, 1)) && sent.Count(s => Same(s, packet, 1)) >= 2) queries.Add(packet);
        }
        return queries.Take(MaxQueries).ToList();
    }

    /// <summary>How many bytes differ, or -1 when the lengths differ.</summary>
    static int Differences(byte[] a, byte[] b) =>
        a.Length != b.Length ? -1 : Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]);

    /// <summary>
    /// Builds the two state replies from what the headphones answered in each state. Bytes that
    /// change within a state become ??, and so does all that follows the first byte telling the
    /// states apart: usually other settings and the checksum. Returns null if nothing tells them apart.
    /// </summary>
    public static (int[] On, int[] Off)? BuildReplies(List<byte[]> whenOn, List<byte[]> whenOff)
    {
        // The two closest replies, one per state, are the same message with the state changed.
        // Different messages can look alike: the Edifier X2s sends two that differ by 3 bytes
        var pairs = whenOn.SelectMany(on => whenOff.Select(off => (On: on, Off: off, Count: Differences(on, off))))
            .Where(p => p.Count is >= 1 and <= MaxReplyDifferences).OrderBy(p => p.Count);
        foreach (var pair in pairs)
        {
            var patternOn = Merge(SameMessage(whenOn, pair.On));
            var patternOff = Merge(SameMessage(whenOff, pair.Off));
            int first = Enumerable.Range(0, patternOn.Length).FirstOrDefault(i => patternOn[i] != Hex.Any && patternOff[i] != Hex.Any && patternOn[i] != patternOff[i], -1);
            if (first < 0) continue;
            for (int i = first + 1; i < patternOn.Length; i++) patternOn[i] = patternOff[i] = Hex.Any;
            return (patternOn, patternOff);
        }
        return null;
    }

    /// <summary>The replies equal to this one, apart from a sequence number and a checksum.</summary>
    static List<byte[]> SameMessage(List<byte[]> samples, byte[] reply) =>
        samples.Where(s => Differences(s, reply) is >= 0 and <= 2).ToList();

    static int[] Merge(List<byte[]> samples) =>
        Enumerable.Range(0, samples[0].Length).Select(i => samples.All(s => s[i] == samples[0][i]) ? samples[0][i] : Hex.Any).ToArray();
}
