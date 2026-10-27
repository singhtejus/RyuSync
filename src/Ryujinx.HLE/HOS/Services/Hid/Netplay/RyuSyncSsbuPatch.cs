using System;
using System.Buffers.Binary;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // Experimental, in-memory patch. Offsets are relative to SSBU 1.0.0's main NSO.
    // This controls the application loop, not every worker or source of randomness.
    internal static class RyuSyncSsbuPatch
    {
        public const string BuildId = "FD471EB458C915E7CD13BBB67420632D00000000000000000000000000000000";
        public const int InputSvc = 0x7F00;
        public const int UpdateSvc = 0x7F01;
        public const int CompleteSvc = 0x7F02;
        public const int BattleSvc = 0x7F03;
        public const int BattleOffset = 0xE7D8D0;
        public const int ResetFirstSvc = 0x7F04;
        public const int ResetSecondSvc = 0x7F05;
        public const int ResetFirstOffset = 0x2B9D6FC;
        public const int ResetSecondOffset = 0x2BA2B74;
        public const int InputOffset = 0x278EC1C;
        public const int UpdateOffset = 0x278F07C;
        public const int CompleteOffset = 0x278F084;

        // Validate every site before changing any byte. Retain the catch-up queue's
        // accounting, but remove its extra application call (which reused input).
        internal static readonly (int Offset, uint Expected, uint Replacement)[] Instructions =
        [
            // Observe the original counter reset; the hook performs the same store.
            (ResetFirstOffset, 0xB900073F, Svc(ResetFirstSvc)),
            (ResetSecondOffset, 0xB900053F, Svc(ResetSecondSvc)),
            (0x278EC0C, 0xB9430915, 0x52800035), // ldr w21 -> mov w21, #1
            (0x278EC14, 0x540023EB, 0x540023EB),
            (0x278EC18, 0x540006C1, 0x540006C1),
            (InputOffset, 0x3953D328, Svc(InputSvc)),
            (0x278EC20, 0x36001C28, 0x140000E1), // always take the fresh-input path
            (0x278EFA4, 0x97FD59FB, 0x97FD59FB), // original controller poll
            (0x278EFA8, 0x9400149A, 0x9400149A), // original input conversion
            (UpdateOffset, 0xF94147E0, Svc(UpdateSvc)),
            (0x278F080, 0x97FF7970, 0x97FF7970), // original application call
            (CompleteOffset, 0xB944FB48, Svc(CompleteSvc)),
            (0x278F088, 0x11000508, 0x11000508),
            (0x278F08C, 0xB904FB48, 0xB904FB48),
            (0x278F1AC, 0x97FF7925, 0xD503201F), // nop: no second update with cached input
            // Gate before battle-clock writes and phase dispatch. x16 is the ABI scratch register.
            (BattleOffset, 0x35001576, Svc(BattleSvc)),
            (0xE7D8D4, 0xB001A528, 0xD61F0200), // br x16: original path or post-battle housekeeping
            (0xE7DB00, 0x350003F6, 0x17FFFF74), // alternate cleanup path must also enter the gate
            (0xE7DB04, 0x17FFFF74, 0x17FFFF74),
            // Read-only guards for the diagnostic clock addresses and their writers.
            (0xE7D848, 0x9001A4E8, 0x9001A4E8),
            (0xE7D84C, 0xB94CC116, 0xB94CC116),
            (0xE7DA98, 0xF002AEA8, 0xF002AEA8),
            (0xE7DA9C, 0x91228108, 0x91228108),
            (0xE7DAC8, 0xB9000509, 0xB9000509),
            (0xE7DAD8, 0xB9000D09, 0xB9000D09),
            (0xE7DB34, 0xB900050A, 0xB900050A),
            (0xE7DB70, 0xB9000D09, 0xB9000D09),
            (0xE7DB90, 0x94006178, 0x94006178),
        ];

        public static void Apply(Span<byte> text, uint textOffset, string buildId)
        {
            if (buildId != BuildId || textOffset != 0)
            {
                throw new InvalidOperationException("RyuSync's experimental update synchronization requires the supported SSBU 1.0.0 executable. Disconnect to play other versions.");
            }

            foreach (var instruction in Instructions)
            {
                if (instruction.Offset > text.Length - sizeof(uint) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(text.Slice(instruction.Offset, sizeof(uint))) != instruction.Expected)
                {
                    throw new InvalidOperationException($"RyuSync cannot verify SSBU code at 0x{instruction.Offset:X}. No synchronization patch was applied. Disable conflicting code mods.");
                }
            }

            foreach (var instruction in Instructions)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(text.Slice(instruction.Offset, sizeof(uint)), instruction.Replacement);
            }
        }

        private static uint Svc(int id) => 0xD4000001U | ((uint)id << 5);
    }
}
