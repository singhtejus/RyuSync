using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

// Test both sides of a real TCP connection and exchange UDP controller packets.
// Reflection avoids adding a production API solely for ephemeral test ports.
await CheckInputExchange(RyuSyncRole.Host);
await CheckInputExchange(RyuSyncRole.Guest);
Console.WriteLine("PASS: host and guest both exchange input and preserve P1/P2 assignments.");
await LaunchTests.Run();

static async Task CheckInputExchange(RyuSyncRole role)
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
            await control.WriteLineAsync($"INVITE|{RyuSyncSession.ProtocolVersion}|{RyuSyncSession.DefaultInputDelayFrames}");
            await WaitUntil(() => session.InvitationPending, "Guest invitation was not received");
            session.AcceptInvitation();
        }

        await WaitUntil(() => session.IsSessionEstablished, "Session was not accepted");
        session.SetReady(true);
        session.NotifyGameStarted();
        await control.WriteLineAsync("READY|1");
        await control.WriteLineAsync("GAME|1");
        await WaitUntil(() => session.GameplayClockStarted, "Launch barrier was not released");
        if (session.Status.StartsWith("Synchronized"))
        {
            throw new Exception("Session claimed synchronization before receiving peer input");
        }

        GamepadInput local = new() { PlayerId = PlayerIndex.Player1, Buttons = ControllerKeys.A };
        session.TryGetSynchronizedInputs(local, out _, out _);
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
        await WaitUntil(() => session.TryGetSynchronizedInputs(local, out p1, out p2) &&
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
    }
    finally
    {
        session.Disconnect();
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
