using Ryujinx.Common.Logging;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    public sealed class RyuSyncNpadDevices : NpadDevices
    {
        private bool _pausedForBarrier;
        private bool _pausedForInput;
        private bool _gameStartAnnounced;
        private long _lastAppliedPlaybackTick = -1;

        public RyuSyncNpadDevices(Switch device, bool active = true) : base(device, active)
        {
        }

        public new void Configure(params System.ReadOnlySpan<ControllerConfig> configs)
        {
            RyuSyncSession session = RyuSyncSession.Instance;

            if (!session.IsSessionEstablished)
            {
                base.Configure(configs);
                return;
            }

            // RyuSync v1 is deliberately two-player SSBU only. Both peers expose the exact same
            // virtual controller topology to Horizon regardless of which physical controller is local.
            ControllerConfig[] netplayConfigs =
            [
                new ControllerConfig
                {
                    Player = PlayerIndex.Player1,
                    Type = ControllerType.ProController,
                },
                new ControllerConfig
                {
                    Player = PlayerIndex.Player2,
                    Type = ControllerType.ProController,
                },
            ];

            base.Configure(netplayConfigs);

            if (!_gameStartAnnounced)
            {
                _gameStartAnnounced = true;
                session.NotifyGameStarted();

                if (!session.GameplayClockStarted && !_device.System.IsPaused)
                {
                    _device.System.TogglePauseEmulation(true);
                    _pausedForBarrier = true;
                    Logger.Info?.Print(LogClass.Hid, "RyuSync paused at the launch barrier.");
                }
            }
        }

        public new void Update(IList<GamepadInput> states)
        {
            RyuSyncSession session = RyuSyncSession.Instance;

            if (!session.IsSessionEstablished)
            {
                ReleaseNetplayPauseIfNeeded();
                base.Update(states);
                return;
            }

            GamepadInput localInput = default;
            bool foundLocalInput = false;

            // Each computer's configured local Player 1 controller is the physical source.
            // The session remaps it to shared P1 for the host or shared P2 for the guest.
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].PlayerId == PlayerIndex.Player1)
                {
                    localInput = states[i];
                    foundLocalInput = true;
                    break;
                }
            }

            if (!foundLocalInput)
            {
                PauseForInput();
                return;
            }

            if (!session.GameplayClockStarted)
            {
                PauseForBarrier();
                return;
            }

            if (_pausedForBarrier)
            {
                _device.System.TogglePauseEmulation(false);
                _pausedForBarrier = false;
                Logger.Info?.Print(LogClass.Hid, "RyuSync launch barrier released.");
            }

            if (session.TryGetSynchronizedInputs(localInput, out GamepadInput p1, out GamepadInput p2))
            {
                if (_pausedForInput)
                {
                    _device.System.TogglePauseEmulation(false);
                    _pausedForInput = false;
                    Logger.Debug?.Print(LogClass.Hid, $"RyuSync input resumed at tick {session.PlaybackTick}.");
                }

                // NpadManager itself runs roughly every millisecond. Writing the same synchronized
                // state on every call would let the two machines accumulate different HID sampling
                // counts. Commit exactly one P1/P2 batch for each 60 Hz RyuSync tick instead.
                long playbackTick = session.PlaybackTick;
                if (playbackTick != _lastAppliedPlaybackTick)
                {
                    GamepadInput[] synchronizedInputs = [p1, p2];
                    base.Update(synchronizedInputs);
                    _lastAppliedPlaybackTick = playbackTick;
                }
            }
            else
            {
                PauseForInput();
            }
        }

        public new void UpdateSixAxis(IList<SixAxisInput> states)
        {
            if (!RyuSyncSession.Instance.IsSessionEstablished)
            {
                base.UpdateSixAxis(states);
            }

            // Motion is intentionally unsupported in RyuSync v1. Do not feed independently sampled
            // motion data into the two emulators while netplay is active.
        }

        private void PauseForBarrier()
        {
            if (_pausedForBarrier || _device.System.IsPaused)
            {
                return;
            }

            _device.System.TogglePauseEmulation(true);
            _pausedForBarrier = true;
        }

        private void PauseForInput()
        {
            if (_pausedForInput || _device.System.IsPaused)
            {
                return;
            }

            _device.System.TogglePauseEmulation(true);
            _pausedForInput = true;
            Logger.Debug?.Print(LogClass.Hid, "RyuSync waiting for the remote input tick.");
        }

        private void ReleaseNetplayPauseIfNeeded()
        {
            if ((_pausedForBarrier || _pausedForInput) && _device.System.IsPaused)
            {
                _device.System.TogglePauseEmulation(false);
            }

            _pausedForBarrier = false;
            _pausedForInput = false;
            _lastAppliedPlaybackTick = -1;
        }
    }
}
