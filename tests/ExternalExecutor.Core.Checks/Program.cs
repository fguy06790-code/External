using ExternalExecutor.Core;

int checks = 0;

Check("empty payload", () => Equal(0, ClientsListParser.Parse(null).Count));
Check("whitespace payload", () => Equal(0, ClientsListParser.Parse(" \r\n\t").Count));
Check("LF and CRLF clients", () =>
{
    var clients = ClientsListParser.Parse("321\t1\r\n654\t0\n");
    Equal(2, clients.Count);
    Equal(new RobloxClient(321, true), clients[0]);
    Equal(new RobloxClient(654, false), clients[1]);
});
Check("malformed records are ignored", () =>
{
    var clients = ClientsListParser.Parse("bad\nwat\t1\n0\t1\n-1\t1\n4294967296\t1\n5\ttrue\n6\t2\n7\t1\textra\n8\n9\t0\n");
    Equal(1, clients.Count);
    Equal(new RobloxClient(9, false), clients[0]);
});
Check("duplicate PID uses last valid state and retains ordering", () =>
{
    var clients = ClientsListParser.Parse("9\t0\n10\t1\n9\t1\n10\tinvalid\n");
    Equal(2, clients.Count);
    Equal(new RobloxClient(9, true), clients[0]);
    Equal(new RobloxClient(10, true), clients[1]);
});
Check("surrounding field whitespace and uint maximum", () =>
{
    var clients = ClientsListParser.Parse(" 4294967295 \t 1 \r\n");
    Equal(new RobloxClient(uint.MaxValue, true), clients.Single());
});
Check("auto attach attempts first discovery and newly appearing clients once", () =>
{
    var tracker = new ClientAutoAttacher();
    var called = new List<uint>();
    bool Attach(uint pid) { called.Add(pid); return true; }
    var first = tracker.Observe([new(11, false)], true, Attach);
    Equal(new AutoAttachAttempt(11, true, null), first.Single());
    Equal(0, tracker.Observe([new(11, false)], true, Attach).Count);
    var next = tracker.Observe([new(11, false), new(12, false)], true, Attach);
    Equal(new AutoAttachAttempt(12, true, null), next.Single());
    Equal("11,12", string.Join(',', called));
});
Check("disabled discovery is remembered when enabling auto attach", () =>
{
    var tracker = new ClientAutoAttacher();
    var called = new List<uint>();
    bool Attach(uint pid) { called.Add(pid); return true; }
    Equal(0, tracker.Observe([new(21, false)], false, Attach).Count);
    Equal(0, tracker.Observe([new(21, false)], true, Attach).Count);
    Equal(0, tracker.Observe([new(21, false), new(22, false)], false, Attach).Count);
    var attempts = tracker.Observe([new(21, false), new(22, false), new(23, false)], true, Attach);
    Equal((uint)23, attempts.Single().ProcessId);
    Equal("23", string.Join(',', called));
});
Check("disappearing clients can receive a new attempt when they return", () =>
{
    var tracker = new ClientAutoAttacher();
    int calls = 0;
    bool Attach(uint _) { calls++; return false; }
    tracker.Observe([new(31, false), new(32, false)], true, Attach);
    Equal(0, tracker.Observe([new(32, false)], true, Attach).Count);
    Equal((uint)31, tracker.Observe([new(31, false), new(32, false)], true, Attach).Single().ProcessId);
    Equal(3, calls);
});
Check("attached clients, duplicate records, and PID zero do not cause extra attempts", () =>
{
    var tracker = new ClientAutoAttacher();
    var called = new List<uint>();
    bool Attach(uint pid) { called.Add(pid); return true; }
    var attempts = tracker.Observe(
        [new(0, false), new(41, true), new(42, false), new(42, false), new(43, false), new(43, true)],
        true, Attach);
    Equal((uint)42, attempts.Single().ProcessId);
    Equal("42", string.Join(',', called));
    Equal(0, tracker.Observe([new(41, false), new(42, false), new(43, false)], true, Attach).Count);
});
Check("auto attach reports failures once and continues after a callback exception", () =>
{
    var tracker = new ClientAutoAttacher();
    int calls = 0;
    bool Attach(uint pid)
    {
        calls++;
        if (pid == 52) throw new InvalidOperationException("native module unavailable");
        return pid == 53;
    }
    RobloxClient[] clients = [new(51, false), new(52, false), new(53, false)];
    var attempts = tracker.Observe(clients, true, Attach);
    Equal(new AutoAttachAttempt(51, false, "Attach returned false."), attempts[0]);
    Equal(new AutoAttachAttempt(52, false, "native module unavailable"), attempts[1]);
    Equal(new AutoAttachAttempt(53, true, null), attempts[2]);
    Equal(0, tracker.Observe(clients, true, Attach).Count);
    Equal(3, calls);
});
Check("invalid auto attach arguments do not replace the observed snapshot", () =>
{
    var tracker = new ClientAutoAttacher();
    tracker.Observe([new(61, false)], false, _ => true);
    Throws<ArgumentNullException>(() => tracker.Observe(null!, true, _ => true));
    Throws<ArgumentNullException>(() => tracker.Observe([], false, null!));
    Throws<ArgumentException>(() => tracker.Observe([new(62, false), null!], true, _ => true));
    Equal(0, tracker.Observe([new(61, false)], true, _ => throw new Exception("Must remain known")).Count);
    Equal((uint)62, tracker.Observe([new(61, false), new(62, false)], true, _ => true).Single().ProcessId);
});
Check("auto attach snapshot is current before callbacks run", () =>
{
    var tracker = new ClientAutoAttacher();
    RobloxClient[] clients = [new(71, false), new(72, false)];
    var attempts = tracker.Observe(clients, true, _ =>
    {
        Equal(0, tracker.Observe(clients, true, _ => throw new Exception("Cannot repeat discovery")).Count);
        return true;
    });
    Equal(2, attempts.Count);
});
Check("PID zero is rejected before any native operation", () =>
{
    var api = new QuorumExecutorApi();
    Throws<ArgumentOutOfRangeException>(() => api.Attach(0));
    Throws<ArgumentOutOfRangeException>(() => api.IsAttachedByPid(0));
    Throws<ArgumentOutOfRangeException>(() => api.DetachByPid(0));
    Throws<ArgumentOutOfRangeException>(() => api.Execute(0, "print('hello')"));
});
Check("empty and NUL scripts are rejected before any native operation", () =>
{
    var api = new QuorumExecutorApi();
    Throws<ArgumentException>(() => api.ExecuteScriptAll(" \r\n"));
    Throws<ArgumentException>(() => api.ExecuteScriptAll("print('one')\0print('two')"));
    Throws<ArgumentNullException>(() => api.ExecuteScriptAll(null!));
});
if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
{
    Check("unsupported platform fails before loading the DLL", () =>
        Throws<PlatformNotSupportedException>(() => new QuorumExecutorApi().GetClients()));
}

Console.WriteLine($"Passed {checks} checks. No native module was loaded.");
return;

void Check(string name, Action action)
{
    try
    {
        action();
        checks++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        Environment.Exit(1);
    }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
