using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

// Test both sides of a real TCP connection and exchange UDP controller packets.
HookTests.Run();
BattleGateTests.Run();
DiagnosticTests.Run();
await HookRuntimeTests.Run();
// Reflection avoids adding a production API solely for ephemeral test ports.
for (int delay = 0; delay <= RyuSyncSession.MaximumInputDelayFrames; delay++)
{
    await CheckInputExchange(RyuSyncRole.Host, delay);
    await CheckInputExchange(RyuSyncRole.Guest, delay);
    await CheckInputExchange(RyuSyncRole.Host, delay, simulationSteps: true);
    await CheckInputExchange(RyuSyncRole.Guest, delay, simulationSteps: true);
}
Console.WriteLine("PASS: host and guest exchange input with every buffer size, preserve P1/P2 assignments and lock the buffer while connected.");
Console.WriteLine("PASS: simulation steps advance without a clock deadline, latch repeated reads, wait for missing input and reject skipped steps or mixed clocks.");
await LaunchTests.Run();

static async Task CheckInputExchange(RyuSyncRole role, int delay, bool simulationSteps = false)
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    using TcpClient outgoing = new();
    await outgoing.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
    using TcpClient incoming = await listener.AcceptTcpClientAsync();
    TcpClient owner = role == RyuSyncRole.Host ? outgoing : incoming;
    TcpClient peer = role == RyuSyncRole.Host ? incoming : outgoing;
    using UdpClient peerInput = new(new IPEndPoint(IPAddress.Loopback, 0));
    using StreamWriter control = new(peer.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

    RyuSyncSession session = (RyuSyncSession)Activator.CreateInstance(typeof(RyuSyncSession), nonPublic: true)!;
    if (!session.CanSetInputDelayFrames) throw new Exception("Buffer is locked while disconnected");
    session.SetInputDelayFrames(-1);
    if (session.InputDelayFrames != 0) throw new Exception("Negative buffer was accepted");
    session.SetInputDelayFrames(RyuSyncSession.MaximumInputDelayFrames + 1);
    if (session.InputDelayFrames != RyuSyncSession.MaximumInputDelayFrames) throw new Exception("Oversized buffer was accepted");
    session.SetInputDelayFrames(role == RyuSyncRole.Host ? delay : (delay + 1) % 9);
    const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    typeof(RyuSyncSession).GetMethod("InitializeControlConnection", privateInstance)!.Invoke(session, [owner, role]);

    try
    {
        FieldInfo endpointField = typeof(RyuSyncSession).GetField("_remoteInputEndpoint", privateInstance)!;
        IPEndPoint endpoint = (IPEndPoint)endpointField.GetValue(session)!;
        Console.WriteLine($"{role}: TCP peer {owner.Client.RemoteEndPoint}; UDP peer address {endpoint.Address}");
        endpointField.SetValue(session, new IPEndPoint(endpoint.Address, ((IPEndPoint)peerInput.Client.LocalEndPoint!).Port));

        if (role == RyuSyncRole.Host)
        {
            await control.WriteLineAsync("ACCEPT");
        }
        else
        {
            await control.WriteLineAsync($"INVITE|{RyuSyncSession.ProtocolVersion}|{delay}");
            await WaitUntil(() => session.InvitationPending, "Guest invitation was not received");
            session.AcceptInvitation();
        }

        await WaitUntil(() => session.IsSessionEstablished, "Session was not accepted");
        if (session.CanSetInputDelayFrames) throw new Exception("Buffer is editable while connected");
        session.SetInputDelayFrames((delay + 1) % 9);
        if (session.InputDelayFrames != delay) throw new Exception("Buffer negotiation or connection lock failed");
        session.SetReady(true);
        session.NotifyGameStarted();
        await control.WriteLineAsync("READY|1");
        await control.WriteLineAsync("GAME|1");
        await WaitUntil(() => session.GameplayClockStarted, "Launch barrier was not released");
        if (simulationSteps)
        {
            const string state = "00010000,00000000,00000101,00000000,3F800000,00000000";
            session.PublishBattle(0, state);
            if (session.TryDecideBattle(0, out _)) throw new Exception("Battle advanced without peer readiness");
            await control.WriteLineAsync("BATTLE|0|-");
            await WaitUntil(() => session.TryDecideBattle(0, out _), "Loading vote was not received");
            session.TryDecideBattle(0, out bool advance);
            if (advance) throw new Exception("Battle advanced while peer was loading");
            session.FinishBattle(0);
            session.PublishBattle(1, state);
            await control.WriteLineAsync($"BATTLE|1|{state}");
            await WaitUntil(() => session.TryDecideBattle(1, out bool ready) && ready, "Matching readiness did not release battle");
            session.FinishBattle(1);
            using StreamReader received = new(peer.GetStream(), Encoding.UTF8, false, 1024, true);
            using CancellationTokenSource voteTimeout = new(TimeSpan.FromSeconds(5));
            for (int step = 0; step < 2; step++)
            {
                string line;
                do { line = await received.ReadLineAsync(voteTimeout.Token); }
                while (line != null && !line.StartsWith("BATTLE|"));
                if (line != $"BATTLE|{step}|{state}") throw new Exception("Battle vote was not sent exactly once");
            }
        }
        if (session.Status.StartsWith("Synchronized"))
        {
            throw new Exception("Session claimed synchronization before receiving peer input");
        }

        GamepadInput local = new() { PlayerId = PlayerIndex.Player1, Buttons = ControllerKeys.A };
        long nextStep = 0;
        if (simulationSteps)
        {
            // A simulated step must proceed even if no computer-clock tick is due.
            typeof(RyuSyncSession).GetField("_nextInputTickTimestamp", privateInstance)!.SetValue(session, long.MaxValue);
        }
        ReadInput(out _, out _);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        UdpReceiveResult sent = await peerInput.ReceiveAsync(timeout.Token);
        if (sent.Buffer.Length < 34 || sent.Buffer[0] != RyuSyncSession.ProtocolVersion)
        {
            throw new Exception("Peer did not receive a controller packet");
        }

        // Return a distinct button on the first scheduled input tick.
        using MemoryStream packet = new();
        using (BinaryWriter writer = new(packet, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)RyuSyncSession.ProtocolVersion);
            writer.Write((byte)1);
            writer.Write((long)session.InputDelayFrames);
            writer.Write((long)ControllerKeys.B);
            for (int axis = 0; axis < 4; axis++)
            {
                writer.Write(0);
            }
        }

        byte[] data = packet.ToArray();
        await peerInput.SendAsync(data, data.Length, sent.RemoteEndPoint);
        GamepadInput p1 = default;
        GamepadInput p2 = default;
        await WaitUntil(() => ReadInput(out p1, out p2) &&
                              session.PlaybackTick > session.InputDelayFrames,
                        "Playback stalled waiting for valid peer input");

        ControllerKeys expectedP1 = role == RyuSyncRole.Host ? ControllerKeys.A : ControllerKeys.B;
        ControllerKeys expectedP2 = role == RyuSyncRole.Host ? ControllerKeys.B : ControllerKeys.A;
        if (p1.PlayerId != PlayerIndex.Player1 || p2.PlayerId != PlayerIndex.Player2 ||
            p1.Buttons != expectedP1 || p2.Buttons != expectedP2)
        {
            throw new Exception($"{role}: controller assignments were swapped or inputs were lost");
        }

        if (!session.Status.StartsWith("Synchronized"))
        {
            throw new Exception("Session did not report successful input synchronization");
        }

        if (simulationSteps)
        {
            long tick = session.PlaybackTick;
            GamepadInput changed = new() { PlayerId = PlayerIndex.Player1, Buttons = ControllerKeys.X };
            typeof(RyuSyncSession).GetField("_nextInputTickTimestamp", privateInstance)!.SetValue(session, 0L);
            for (int read = 0; read < 5; read++)
            {
                if (!session.TryGetInputsForSimulationStep(nextStep - 1, changed, out GamepadInput repeatedP1, out GamepadInput repeatedP2) ||
                    repeatedP1.Buttons != expectedP1 || repeatedP2.Buttons != expectedP2 || session.PlaybackTick != tick)
                {
                    throw new Exception("A repeated step changed or consumed its latched input pair");
                }
            }

            // The peer has sent only the first playable step. Every later step
            // must stay pending, even though an older input pair is available.
            if (session.TryGetInputsForSimulationStep(nextStep, local, out _, out _) || session.PlaybackTick != tick)
            {
                throw new Exception("A missing peer input advanced the simulation step");
            }

            MustReject(() => session.TryGetInputsForSimulationStep(nextStep + 1, local, out _, out _));
            MustReject(() => session.TryGetSynchronizedInputs(local, out _, out _));

            // Deliver the missing packet after the step has stalled. It must
            // complete that same step with the originally queued local input.
            BitConverter.TryWriteBytes(data.AsSpan(2, sizeof(long)), nextStep);
            await peerInput.SendAsync(data, data.Length, sent.RemoteEndPoint);
            await WaitUntil(() => ReadInput(out p1, out p2), "A pending simulation step did not resume when its peer input arrived");
            if (p1.Buttons != expectedP1 || p2.Buttons != expectedP2 || session.PlaybackTick != tick + 1)
            {
                throw new Exception("The resumed step skipped or changed its input pair");
            }
            MustReject(() => session.TryGetInputsForSimulationStep(0, local, out _, out _));
        }
        else
        {
            MustReject(() => session.TryGetInputsForSimulationStep(0, local, out _, out _));
        }

        bool ReadInput(out GamepadInput first, out GamepadInput second)
        {
            if (!simulationSteps) return session.TryGetSynchronizedInputs(local, out first, out second);
            bool ready = session.TryGetInputsForSimulationStep(nextStep, local, out first, out second);
            if (ready) nextStep++;
            return ready;
        }

        static void MustReject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("An invalid step or clock change was accepted");
        }
    }
    finally
    {
        session.Disconnect();
        if (!session.CanSetInputDelayFrames) throw new Exception("Disconnect did not unlock the buffer");
        peer.Close();
    }
}

static async Task WaitUntil(Func<bool> condition, string failure)
{
    Stopwatch elapsed = Stopwatch.StartNew();
    while (!condition())
    {
        if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
        {
            throw new TimeoutException(failure);
        }

        await Task.Delay(5);
    }
}
