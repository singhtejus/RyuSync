using Ryujinx.HLE.HOS.Services.Hid.Netplay;

internal static class BattleGateTests
{
    public static void Run()
    {
        const string ready = "00010000,00000000,00000101,00000000,3F800000,00000000";
        RyuSyncBattleGate host = new(), guest = new();
        // Reproduce unequal entry into battle without allowing either side to
        // advance alone. Loading can finish its outer update without waiting.
        for (long step = 0; step < 100; step++)
        {
            string hostState = step < 54 ? "-" : ready;
            host.Publish(step, hostState);
            guest.Publish(step, ready);
            Require(!guest.TryDecide(step, out _), "Missing readiness was treated as permission");
            host.Receive(step, ready);
            guest.Receive(step, hostState);
            Require(host.TryDecide(step, out bool h) && guest.TryDecide(step, out bool g) &&
                h == g && h == (step >= 54), "Peers advanced on different updates");
            Require(!host.Publish(step, "-"), "Completion overwrote an existing vote");
            host.Finish(step);
            guest.Finish(step);
        }
        host.Receive(99, ready); // A delayed vote cannot affect the next update.
        host.Publish(100, ready);
        Require(!host.TryDecide(100, out _), "Old readiness released a new update");
        host.Receive(100, ready.Replace("3F800000", "3F000000"));
        Reject(() => host.TryDecide(100, out _));
        Reject(() => host.Receive(100, ready));
        Reject(() => guest.Publish(102, ready));
        Console.WriteLine("PASS: battle readiness tolerates unequal loading, waits for the correct update and rejects differing battle state.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid battle agreement was accepted");
    }
}
