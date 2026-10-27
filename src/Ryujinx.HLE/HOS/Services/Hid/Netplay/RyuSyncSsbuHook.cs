using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using System;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    internal sealed class RyuSyncSsbuHook
    {
        private readonly Switch _device;
        private readonly ulong _mainAddress;
        private readonly RyuSyncLaunchTransfer _connection;
        private readonly RyuSyncStepSequence _sequence = new();
        private KThread _thread;
        private long _nextStart;
        private bool _failed;
        private readonly RyuSyncDiagnostics _diagnostics;
        private RyuSyncBattleClock _before;
        private GamepadInput _p1;
        private GamepadInput _p2;
        private bool _inputsValid;
        private bool _diagnosticFailed;
        private bool _battleSeen;

        public RyuSyncSsbuHook(Switch device, ulong mainAddress, RyuSyncDiagnostics diagnostics = null)
        {
            _device = device;
            _mainAddress = mainAddress;
            _connection = RyuSyncSession.Instance.LaunchTransfer;
            _diagnostics = diagnostics;
        }

        public bool Handle(IExecutionContext context, KThread thread, ulong address, int id)
        {
            int offset = id switch
            {
                RyuSyncSsbuPatch.InputSvc => RyuSyncSsbuPatch.InputOffset,
                RyuSyncSsbuPatch.UpdateSvc => RyuSyncSsbuPatch.UpdateOffset,
                RyuSyncSsbuPatch.CompleteSvc => RyuSyncSsbuPatch.CompleteOffset,
                RyuSyncSsbuPatch.BattleSvc => RyuSyncSsbuPatch.BattleOffset,
                _ => -1,
            };
            if (context.IsAarch32 || offset < 0 || address != _mainAddress + (ulong)offset)
            {
                return false;
            }

            RyuSyncSession session = RyuSyncSession.Instance;
            try
            {
                _thread ??= thread;
                if (_thread != thread || _failed || !session.IsSessionEstablished ||
                    !ReferenceEquals(_connection, session.LaunchTransfer))
                {
                    throw new InvalidOperationException("The RyuSync connection or SSBU update thread changed. Stop the game and reconnect.");
                }

                if (id == RyuSyncSsbuPatch.InputSvc)
                {
                    _sequence.Begin();
                    _battleSeen = false;
                    long started = Stopwatch.GetTimestamp();
                    bool reportedWait = false;
                    while (!thread.TerminationRequested && context.Running)
                    {
                        if (!session.IsSessionEstablished || !ReferenceEquals(_connection, session.LaunchTransfer))
                        {
                            throw new InvalidOperationException("RyuSync disconnected while waiting for input. Stop the game to leave the session.");
                        }
                        long now = Stopwatch.GetTimestamp();
                        // Host time only caps speed. It never selects or skips an input pair.
                        if (now >= _nextStart && _device.Hid.Npads.TryCommitSimulationStep(_sequence.Step))
                        {
                            _nextStart = now + Stopwatch.Frequency / 60;
                            _sequence.InputsCommitted();
                            // Preserve the replaced ldrb, even though the next branch is unconditional.
                            context.SetX(8, thread.Owner.CpuMemory.Read<byte>(context.GetX(25) + 0x4F4));
                            return true;
                        }
                        if (!reportedWait && now - started > 5 * Stopwatch.Frequency)
                        {
                            reportedWait = true;
                            Logger.Warning?.Print(LogClass.Hid, $"RyuSync is waiting at update {_sequence.Step}. Check both controllers and the connection. Stop the game to cancel.");
                        }
                        // Yield the emulated core, not just the host thread. Shutdown can wake this wait.
                        thread.Sleep(1_000_000);
                    }
                    context.StopRunning();
                }
                else if (id == RyuSyncSsbuPatch.UpdateSvc)
                {
                    _sequence.BeforeUpdate();
                    context.SetX(0, thread.Owner.CpuMemory.Read<ulong>(context.GetX(31) + 0x288));
                    CaptureDiagnostics(thread, session, before: true);
                }
                else if (id == RyuSyncSsbuPatch.BattleSvc)
                {
                    GateBattle(context, thread, session);
                }
                else
                {
                    session.FinishBattle(_sequence.Step);
                    CaptureDiagnostics(thread, session, before: false);
                    _sequence.Complete();
                    context.SetX(8, thread.Owner.CpuMemory.Read<uint>(context.GetX(26) + 0x4F8));
                    if (_sequence.Step == 1 || _sequence.Step % 600 == 0)
                    {
                        Logger.Info?.Print(LogClass.Hid, $"RyuSync completed numbered update {_sequence.Step - 1}. Experimental; game-state agreement is not verified.");
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                if (!_failed)
                {
                    _failed = true;
                    _diagnostics?.Fail(ex.Message);
                    Logger.Error?.Print(LogClass.Hid, ex.Message);
                    session.FailSimulation(ex.Message);
                }
                // Never fall back to unsynchronized play or attach a running game to a new session.
                while (!thread.TerminationRequested && context.Running)
                {
                    thread.Sleep(10_000_000);
                }
                context.StopRunning();
            }
            return true;
        }

        private void GateBattle(IExecutionContext context, KThread thread, RyuSyncSession session)
        {
            _sequence.CheckUpdating();
            // Restricted processing must continue: it may be needed to finish a scene transition.
            // The normal path alone advances the battle clock and runs all battle phases.
            if ((uint)context.GetX(22) != 0)
            {
                context.SetX(16, _mainAddress + 0xE7DB7C);
                return;
            }
            if (_battleSeen) throw new InvalidOperationException("SSBU attempted two full battle updates for one input pair.");
            _battleSeen = true;
            RyuSyncBattleClock clock = RyuSyncBattleClock.Read(thread.Owner.CpuMemory.Read<uint>, _mainAddress);
            session.PublishBattle(_sequence.Step, clock.ToString());
            long started = Stopwatch.GetTimestamp();
            while (!thread.TerminationRequested && context.Running)
            {
                if (!session.IsSessionEstablished || !ReferenceEquals(_connection, session.LaunchTransfer))
                    throw new InvalidOperationException("RyuSync disconnected while checking battle readiness.");
                if (session.TryDecideBattle(_sequence.Step, out bool advance))
                {
                    // Skip only clock advancement and phase dispatch. Keep the original
                    // post-battle virtual callback and epilogue on both paths.
                    context.SetX(16, _mainAddress + (advance ? 0xE7D8D8UL : 0xE7DB94UL));
                    if (advance) context.SetX(8, _mainAddress + 0x4322000); // replaced adrp
                    return;
                }
                if (Stopwatch.GetTimestamp() - started > 30 * Stopwatch.Frequency)
                    throw new InvalidOperationException("Timed out checking battle readiness. Export both diagnostics and restart the game.");
                thread.Sleep(1_000_000);
            }
            context.StopRunning();
        }

        private void CaptureDiagnostics(KThread thread, RyuSyncSession session, bool before)
        {
            if (_diagnostics == null || _diagnosticFailed) return;
            try
            {
                RyuSyncBattleClock clock = RyuSyncBattleClock.Read(thread.Owner.CpuMemory.Read<uint>, _mainAddress);
                if (before)
                {
                    _before = clock;
                    _inputsValid = session.GetDiagnosticInputs(_sequence.Step, out _p1, out _p2);
                }
                else
                {
                    _diagnostics.Add(new(_sequence.Step, _inputsValid, _p1, _p2, _before, clock));
                }
            }
            catch (InvalidMemoryRegionException)
            {
                // Instrumentation must not disconnect or change the running game.
                _diagnosticFailed = true;
                _diagnostics.Fail("Battle clock could not be read; capture stopped.");
            }
        }
    }
}
