using Ryujinx.Cpu;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

internal static class HookRuntimeTests
{
    public static async Task Run()
    {
        foreach (string scenario in new[] { "normal", "cancel", "disconnect", "wrong-thread", "wrong-order",
            "battle-ready", "battle-loading", "battle-restricted", "battle-cancel", "battle-mismatch" })
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            using TcpClient client = new();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            RyuSyncSession session = RyuSyncSession.Instance;
            typeof(RyuSyncSession).GetField("_controlClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, client);
            typeof(RyuSyncSession).GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            RyuSyncBattleGate gate = new();
            typeof(RyuSyncSession).GetField("_battleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, gate);
            try
            {
                const ulong main = 0x8504000;
                Switch device = new();
                RyuSyncDiagnostics diagnostics = new("hook-test", RyuSyncRole.Host);
                RyuSyncSsbuHook hook = new(device, main, diagnostics);
                KThread thread = new();
                TestExecutionContext context = new();
                ulong[] original = (ulong[])context.Registers.Clone();
                thread.Owner.CpuMemory.Values[context.GetX(25) + 0x4F4] = (byte)1;
                thread.Owner.CpuMemory.Values[context.GetX(31) + 0x288] = 0x12345678UL;
                thread.Owner.CpuMemory.Values[context.GetX(26) + 0x4F8] = uint.MaxValue;
                foreach (ulong offset in new ulong[] { 0x64548A0, 0x64548A4, 0x64548A8, 0x64548AC, 0x64548B0, 0x4319CC0 })
                {
                    thread.Owner.CpuMemory.Values[main + offset] = 1U;
                }
                int commits = 0;
                device.Hid.Npads.Commit = step =>
                {
                    Require(step == 0, "A waiting hook skipped its update");
                    commits++;
                    return scenario is not ("cancel" or "disconnect") && commits > 1;
                };
                thread.OnSleep = () =>
                {
                    if (scenario == "disconnect") session.Disconnect();
                    if (scenario == "cancel" || !session.IsSessionEstablished) thread.TerminationRequested = true;
                };

                Require(!hook.Handle(context, thread, main, RyuSyncSsbuPatch.InputSvc), "Hook accepted an unpatched address");
                Require(!hook.Handle(context, thread, main + RyuSyncSsbuPatch.InputOffset, 1), "Hook accepted a normal SVC");
                context.IsAarch32 = true;
                Require(!Call(RyuSyncSsbuPatch.InputOffset, RyuSyncSsbuPatch.InputSvc), "Hook accepted a 32-bit process");
                context.IsAarch32 = false;

                if (scenario == "wrong-order")
                {
                    Call(RyuSyncSsbuPatch.UpdateOffset, RyuSyncSsbuPatch.UpdateSvc);
                    Require(!session.IsSessionEstablished && !context.Running, "Invalid call order did not stop the session");
                    continue;
                }
                Call(RyuSyncSsbuPatch.InputOffset, RyuSyncSsbuPatch.InputSvc);
                if (scenario is "cancel" or "disconnect")
                {
                    Require(!context.Running && commits == 1, "Cancelled wait released input or failed to stop");
                    Require(context.Registers.SequenceEqual(original), "Cancelled wait altered guest registers");
                    continue;
                }

                Require(commits == 2 && thread.Sleeps == 1, "Hook did not wait for the input pair");
                Require(context.GetX(8) == 1, "Replaced byte load was not emulated");
                if (scenario == "wrong-thread")
                {
                    KThread other = new();
                    other.OnSleep = () => other.TerminationRequested = true;
                    hook.Handle(context, other, main + RyuSyncSsbuPatch.UpdateOffset, RyuSyncSsbuPatch.UpdateSvc);
                    Require(!session.IsSessionEstablished && !context.Running, "Wrong thread was allowed to update");
                    continue;
                }
                Call(RyuSyncSsbuPatch.UpdateOffset, RyuSyncSsbuPatch.UpdateSvc);
                Require(context.GetX(0) == 0x12345678, "Application argument was not restored");
                if (scenario.StartsWith("battle-"))
                {
                    context.SetX(22, scenario == "battle-restricted" ? 1UL : 0UL);
                    original[22] = context.GetX(22);
                    thread.OnSleep = () =>
                    {
                        if (scenario == "battle-cancel" || !session.IsSessionEstablished)
                            thread.TerminationRequested = true;
                        else
                            gate.Receive(0, scenario == "battle-loading" ? "-" :
                                scenario == "battle-mismatch" ? "different" :
                                RyuSyncBattleClock.Read(thread.Owner.CpuMemory.Read<uint>, main).ToString());
                    };
                    Call(RyuSyncSsbuPatch.BattleOffset, RyuSyncSsbuPatch.BattleSvc);
                    if (scenario is "battle-cancel" or "battle-mismatch")
                    {
                        Require(!context.Running, "A cancelled or mismatched battle was allowed to run");
                        continue;
                    }
                    ulong expected = scenario == "battle-restricted" ? 0xE7DB7CUL :
                        scenario == "battle-loading" ? 0xE7DB94UL : 0xE7D8D8UL;
                    Require(context.GetX(16) == main + expected, "Battle gate selected the wrong continuation");
                    if (scenario == "battle-ready") Require(context.GetX(8) == main + 0x4322000, "Battle adrp was not restored");
                    original[16] = context.GetX(16);
                }
                Call(RyuSyncSsbuPatch.CompleteOffset, RyuSyncSsbuPatch.CompleteSvc);
                Require(context.GetX(8) == uint.MaxValue, "Replaced word load was not zero-extended");
                for (int i = 1; i < 32; i++)
                {
                    if (i != 8) Require(context.GetX(i) == original[i], "Hook clobbered a guest register");
                }
                Require(commits == 2 && context.Running, "Completing an update consumed another input pair");
                Require(diagnostics.Export().Contains("\n0|0|"), "Hook omitted diagnostics or marked missing input metadata as valid");

                bool Call(int offset, int id) => hook.Handle(context, thread, main + (ulong)offset, id);
            }
            finally
            {
                session.Disconnect();
            }
        }
        Console.WriteLine("PASS: runtime hook yields for input, preserves replaced loads, rejects wrong address/thread/order and cancels on shutdown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
