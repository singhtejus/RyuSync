using System;
using System.Globalization;
using System.Text;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    internal readonly record struct RyuSyncBattleClock(uint Flags, uint Counter04, uint SlowBytes,
        uint Counter0C, uint RateBits, uint Mode)
    {
        internal static RyuSyncBattleClock Read(Func<ulong, uint> read, ulong main)
        {
            ulong clock = main + 0x64548A0;
            return new(read(clock), read(clock + 4), read(clock + 8), read(clock + 12),
                read(clock + 16), read(main + 0x4319CC0));
        }

        public override string ToString() =>
            $"{Flags:X8},{Counter04:X8},{SlowBytes:X8},{Counter0C:X8},{RateBits:X8},{Mode:X8}";
    }

    internal readonly record struct RyuSyncDiagnosticSample(long Step, bool InputsValid,
        GamepadInput P1, GamepadInput P2, RyuSyncBattleClock Before, RyuSyncBattleClock After);

    internal readonly record struct RyuSyncResetSample(long ObservedStep, int Phase, int Site,
        ulong Thread, bool UpdateThread, ulong Target, ulong LinkRegister, RyuSyncBattleClock Before);

    // No network traffic, file I/O or log formatting on the emulated update thread.
    // These scalars are observations, not a checksum of the complete game state.
    public sealed class RyuSyncDiagnostics
    {
        public const int Capacity = 8192;
        private readonly object _sync = new();
        private readonly RyuSyncDiagnosticSample[] _samples = new RyuSyncDiagnosticSample[Capacity];
        private readonly string _sessionId;
        private readonly string _role;
        private int _next;
        private int _count;
        private long _total;
        private string _error = string.Empty;
        private readonly RyuSyncResetSample[] _resets = new RyuSyncResetSample[128];
        private long _resetTotal;

        internal void AddReset(RyuSyncResetSample sample)
        {
            lock (_sync) _resets[_resetTotal++ % _resets.Length] = sample;
        }

        internal RyuSyncDiagnostics(string sessionId, RyuSyncRole role)
        {
            _sessionId = sessionId;
            _role = role.ToString();
        }

        internal void Add(RyuSyncDiagnosticSample sample)
        {
            lock (_sync)
            {
                _samples[_next] = sample;
                _next = (_next + 1) % Capacity;
                _count = Math.Min(_count + 1, Capacity);
                _total++;
            }
        }

        internal void Fail(string reason)
        {
            lock (_sync) _error = reason;
        }

        public string Export()
        {
            RyuSyncDiagnosticSample[] samples;
            long total;
            string error;
            RyuSyncResetSample[] resets;
            long resetTotal;
            lock (_sync)
            {
                samples = new RyuSyncDiagnosticSample[_count];
                int start = (_next - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++) samples[i] = _samples[(start + i) % Capacity];
                total = _total;
                error = _error;
                resetTotal = _resetTotal;
                int resetCount = (int)Math.Min(resetTotal, _resets.Length);
                resets = new RyuSyncResetSample[resetCount];
                for (int i = 0; i < resetCount; i++)
                    resets[i] = _resets[(resetTotal - resetCount + i) % _resets.Length];
            }

            StringBuilder output = new();
            output.AppendLine("RYUSYNC_DIAGNOSTICS_V1");
            output.AppendLine($"session={_sessionId}");
            output.AppendLine($"role={_role}");
            output.AppendLine($"build={RyuSyncSsbuPatch.BuildId}");
            output.AppendLine(FormattableString.Invariant($"total={total}"));
            output.AppendLine($"error={error}");
            output.AppendLine(FormattableString.Invariant($"# reset_total={resetTotal}"));
            output.AppendLine("# reset|observed_update|phase|site_relative_to_main|thread|is_update_thread|target_relative_to_main|link_relative_to_main|before_clock");
            foreach (var reset in resets)
                output.AppendLine(FormattableString.Invariant($"# reset|{reset.ObservedStep}|{reset.Phase}|{reset.Site:X}|{reset.Thread}|{(reset.UpdateThread ? 1 : 0)}|{reset.Target:X}|{reset.LinkRegister:X}|{reset.Before}"));
            output.AppendLine("# step|inputs_valid|p1(buttons,lx,ly,rx,ry)|p2|before(flags,c04,slow,c0c,rate,mode)|after");
            foreach (var sample in samples)
            {
                output.AppendLine(string.Join('|', sample.Step.ToString(CultureInfo.InvariantCulture),
                    sample.InputsValid ? "1" : "0", Input(sample.P1), Input(sample.P2),
                    sample.Before.ToString(), sample.After.ToString()));
            }
            return output.ToString();
        }

        private static string Input(GamepadInput input) => FormattableString.Invariant(
            $"{(ulong)input.Buttons:X16},{input.LStick.Dx},{input.LStick.Dy},{input.RStick.Dx},{input.RStick.Dy}");
    }
}
