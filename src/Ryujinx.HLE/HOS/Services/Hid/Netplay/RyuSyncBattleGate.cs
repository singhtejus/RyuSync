using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // Accessed under the session lock. A missing battle callback is an explicit
    // negative vote, so a peer still loading never waits for a callback it cannot reach.
    internal sealed class RyuSyncBattleGate
    {
        private readonly Dictionary<long, string> _remote = new();
        private long _finished = -1;
        private long _published = -1;
        private string _local;

        public bool Publish(long step, string state)
        {
            if (step != _finished + 1) throw new InvalidOperationException("Unexpected RyuSync battle update order.");
            if (_published == step) return false;
            _published = step;
            _local = state;
            return true;
        }

        public void Receive(long step, string state)
        {
            if (step <= _finished || step > _finished + 64) return;
            if (_remote.TryGetValue(step, out string previous) && previous != state)
                throw new InvalidOperationException("Peer changed its RyuSync battle readiness.");
            _remote[step] = state;
        }

        public bool TryDecide(long step, out bool advance)
        {
            advance = false;
            if (_published != step || !_remote.TryGetValue(step, out string remote)) return false;
            if (_local == "-" || remote == "-") return true;
            if (_local != remote)
                throw new InvalidOperationException($"Battle state differs before update {step}. Local {_local}; peer {remote}. Export both diagnostics and restart the game.");
            advance = true;
            return true;
        }

        public void Finish(long step)
        {
            if (_published != step || step != _finished + 1)
                throw new InvalidOperationException("RyuSync battle readiness was not published.");
            _remote.Remove(step);
            _finished = step;
        }
    }
}
