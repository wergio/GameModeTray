using System.Net.Sockets;

namespace GameModeTray;

/// <summary>An open RFCOMM connection for trying packets: sends them and returns what comes back.</summary>
sealed class RfcommProbe(ulong address, int channel, Action<string> trace) : IDisposable
{
    readonly Socket _socket = GameModeDevice.Connect(address, channel);

    /// <summary>Collects what comes back for waitMs, or until the reply is over with stopWhenQuiet.</summary>
    public List<byte[]> Exchange(byte[] packet, int waitMs, bool stopWhenQuiet)
    {
        trace("-> " + Hex.Format(packet));
        _socket.Send(packet);

        var received = new List<byte[]>();
        var chunk = new byte[1024];
        long deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                int n = _socket.Receive(chunk);
                if (n == 0) break;
                received.Add(chunk[..n]);
                trace("<- " + Hex.Format(chunk.Take(n)));
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                if (stopWhenQuiet && received.Count > 0) break;
            }
        }
        return received;
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>The state query and replies that turned out to work, or empty strings.</summary>
sealed record TestOutcome(string Query, string OnReply, string OffReply)
{
    public bool Verified => OnReply.Length > 0;
}

/// <summary>
/// Tries a command pair on the headphones, sending only packets seen in the log. It toggles
/// game mode twice, looks at what the headphones say in each state and ends with game mode on.
/// </summary>
static class CommandTester
{
    // Headphones can ignore a command that comes too soon after the last one (the Edifier X2s
    // does, within a second or two), so each command gets its whole wait even if it replies at once
    const int Cycles = 2, CommandWaitMs = 4000, QueryWaitMs = 600;

    public static TestOutcome Run(Func<byte[], int, bool, List<byte[]>> exchange, CommandPair pair, List<byte[]> queries)
    {
        var afterOn = new List<List<byte[]>>();
        var afterOff = new List<List<byte[]>>();
        var replies = queries.Select(q => (Query: q, On: new List<List<byte[]>>(), Off: new List<List<byte[]>>())).ToList();

        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            afterOff.Add(exchange(pair.Off, CommandWaitMs, false));
            foreach (var r in replies) r.Off.Add(exchange(r.Query, QueryWaitMs, true));
            afterOn.Add(exchange(pair.On, CommandWaitMs, false));
            foreach (var r in replies) r.On.Add(exchange(r.Query, QueryWaitMs, true));
            // Only what told the states apart goes on: most candidates are out after the first round
            replies.RemoveAll(r => Patterns(r.On, r.Off) is null);
        }

        // The shortest reply: a long one holds other settings too, which would change the match later
        var best = replies.Select(r => (r.Query, Patterns: Patterns(r.On, r.Off)!.Value))
            .OrderBy(r => r.Patterns.On.Length).FirstOrDefault();
        if (best.Query is not null)
            return new TestOutcome(Hex.Format(best.Query), Hex.FormatPattern(best.Patterns.On), Hex.FormatPattern(best.Patterns.Off));
        return Patterns(afterOn, afterOff) is var (onReply, offReply)
            ? new TestOutcome("", Hex.FormatPattern(onReply), Hex.FormatPattern(offReply))
            : new TestOutcome("", "", "");
    }

    /// <summary>The state replies, if they recognise every exchange of their state and none of the other.</summary>
    static (int[] On, int[] Off)? Patterns(List<List<byte[]>> whenOn, List<List<byte[]>> whenOff)
    {
        if (CommandFinder.BuildReplies(whenOn.SelectMany(x => x).ToList(), whenOff.SelectMany(x => x).ToList()) is not var (on, off))
            return null;
        static bool Matches(List<byte[]> exchange, int[] pattern) => exchange.Any(d => Hex.LastIndexOf(d, pattern) >= 0);
        bool consistent = whenOn.All(e => Matches(e, on) && !Matches(e, off)) && whenOff.All(e => Matches(e, off) && !Matches(e, on));
        return consistent ? (on, off) : null;
    }
}
