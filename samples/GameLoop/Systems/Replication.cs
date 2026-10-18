using System.Buffers.Binary;
using EasyEcs;

namespace GameLoop;

// Writes every position that changed since this system last ran into the outbox,
// 12 bytes each: the entity id, then X and Y.
// [Changed<Position>] skips the entities that did not move, so a quiet world costs
// almost nothing. The host sends the bytes and clears the outbox.
[UpdateInGroup<ReplicationGroup>]
public sealed class ReplicationSystem : EcsSystem
{
    const int UpdateSize = 12;

    [ForEach, Changed<Position>]
    static void Send(Entity entity, in Position position, ref Outbox outbox)
    {
        Span<byte> bytes = outbox.Bytes.GetSpan(UpdateSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entity.Id.Index);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], position.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], position.Y);
        outbox.Bytes.Advance(UpdateSize);
        outbox.Updates++;
    }
}
