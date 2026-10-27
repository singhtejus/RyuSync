using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Buffers.Binary;

internal static class HookTests
{
    public static void Run()
    {
        byte[] text = new byte[0x2BA2B78];
        foreach (var instruction in RyuSyncSsbuPatch.Instructions)
        {
            Write(instruction.Offset, instruction.Expected);
        }
        byte[] original = (byte[])text.Clone();
        Reject(() => RyuSyncSsbuPatch.Apply(text, 0, "unsupported"));
        Reject(() => RyuSyncSsbuPatch.Apply(text, 4, RyuSyncSsbuPatch.BuildId));
        Reject(() => RyuSyncSsbuPatch.Apply(text.AsSpan(0, 8), 0, RyuSyncSsbuPatch.BuildId));
        Require(text.AsSpan().SequenceEqual(original), "A rejected executable was modified");

        Write(0x278F1AC, 0);
        Reject(() => RyuSyncSsbuPatch.Apply(text, 0, RyuSyncSsbuPatch.BuildId));
        Write(0x278F1AC, 0x97FF7925);
        Require(text.AsSpan().SequenceEqual(original), "Validation failed after partially patching code");

        RyuSyncSsbuPatch.Apply(text, 0, RyuSyncSsbuPatch.BuildId);
        foreach (var instruction in RyuSyncSsbuPatch.Instructions)
        {
            Require(Read(instruction.Offset) == instruction.Replacement, "A patch instruction was not applied");
        }
        Require(Read(0x278EC0C) == 0x52800035, "The loop does not plan exactly one application update");
        Require(0x278EC20 + ((Read(0x278EC20) & 0x03FFFFFF) << 2) == 0x278EFA4,
            "The fresh-input branch targets the wrong instruction");
        Require(Read(0x278F1AC) == 0xD503201F, "Catch-up can still call the application without fresh input");
        foreach (var (offset, id) in new[]
        {
            (RyuSyncSsbuPatch.InputOffset, RyuSyncSsbuPatch.InputSvc),
            (RyuSyncSsbuPatch.UpdateOffset, RyuSyncSsbuPatch.UpdateSvc),
            (RyuSyncSsbuPatch.CompleteOffset, RyuSyncSsbuPatch.CompleteSvc),
            (RyuSyncSsbuPatch.BattleOffset, RyuSyncSsbuPatch.BattleSvc),
            (RyuSyncSsbuPatch.ResetFirstOffset, RyuSyncSsbuPatch.ResetFirstSvc),
            (RyuSyncSsbuPatch.ResetSecondOffset, RyuSyncSsbuPatch.ResetSecondSvc),
        })
        {
            uint op = Read(offset);
            Require((op & 0xFFE0001F) == 0xD4000001 && ((op >> 5) & 0xFFFF) == id, "Invalid SVC encoding");
        }
        Reject(() => RyuSyncSsbuPatch.Apply(text, 0, RyuSyncSsbuPatch.BuildId));

        RyuSyncStepSequence sequence = new();
        Reject(sequence.BeforeUpdate);
        Reject(sequence.Complete);
        Reject(sequence.InputsCommitted);
        for (int step = 0; step < 1000; step++)
        {
            Require(sequence.Step == step, "Update number skipped");
            sequence.Begin();
            Reject(sequence.Begin);
            Reject(sequence.BeforeUpdate); // Still waiting for the pair.
            Reject(sequence.Complete);
            Require(sequence.Step == step, "Waiting advanced the update number");
            sequence.InputsCommitted();
            Reject(sequence.InputsCommitted);
            Reject(sequence.Complete);
            sequence.BeforeUpdate();
            Reject(sequence.BeforeUpdate);
            sequence.Complete();
            Reject(sequence.Complete);
        }
        Console.WriteLine("PASS: SSBU patch validation is atomic; input routing, SVC encoding and numbered update ordering are guarded.");

        uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(offset, 4));
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(offset, 4), value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("An invalid patch or update sequence was accepted");
    }
}
