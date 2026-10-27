using System;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    internal sealed class RyuSyncStepSequence
    {
        private int _phase;
        public int DiagnosticPhase => System.Threading.Volatile.Read(ref _phase);
        public long Step { get; private set; }

        public void Begin() => Move(0, 1);
        public void InputsCommitted() => Move(1, 2);
        public void BeforeUpdate() => Move(2, 3);
        public void CheckUpdating()
        {
            if (_phase != 3) throw new InvalidOperationException("Battle processing occurred outside the numbered application update.");
        }

        public void Complete()
        {
            Move(3, 0);
            Step++;
        }

        private void Move(int expected, int next)
        {
            if (_phase != expected)
            {
                throw new InvalidOperationException($"Unexpected SSBU update order at step {Step}: phase {_phase}, expected {expected}.");
            }
            _phase = next;
        }
    }
}
