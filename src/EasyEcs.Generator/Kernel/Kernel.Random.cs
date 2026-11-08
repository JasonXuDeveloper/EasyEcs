using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// <c>EntityRandom</c> draws of the row's entity as lanes (<c>RandomLanes</c> in Core): <c>Float</c>, <c>Chance</c> and
/// <c>Range</c> of floats over 4-byte lanes, <c>Range</c> of ints with uniform bounds, <c>Bits</c> over 8-byte lanes; the
/// instance members of the method's <c>in EntityRandom</c> and the static ones with a uniform seed and tick; the entity
/// is <c>entity.Id</c> of the method's Entity, the stream a uniform value. Each lane is the scalar draw bit for bit (the
/// same SplitMix64 rounds in 8-byte lanes, the float of 24 bits scaled by a power of two), so the masked lanes of a word
/// and the rows a vector does not fill compute what they would and keep nothing of it.
/// </summary>
internal sealed partial class Kernel
{
    private bool _draws; // the loop draws: it runs where RandomLanes.IsAccelerated (Code.Draws)

    /// <summary>The lanes of an EntityRandom draw, or null when <paramref name="call"/> is no draw (a reason when it is one the loop cannot take).</summary>
    private string? RandomDraw(IInvocationOperation call)
    {
        var m = call.TargetMethod;
        if (!Symbols.Named(m.ContainingType, "EasyEcs.EntityRandom") || m.Name is not ("Bits" or "Float" or "Range" or "Chance")) return null;
        // The instance members draw from the method's EntityRandom (Seed and Tick); the static ones take the seed before
        // the entity and the tick after it: (seed, entity, tick, stream, ...) against (entity, stream, ...).
        int id = m.IsStatic ? 1 : 0, streamAt = m.IsStatic ? 3 : 1, at = streamAt + 1;
        IOperation Arg(int ordinal) => call.Arguments.First(a => a.Parameter?.Ordinal == ordinal).Value;
        string source;
        if (m.IsStatic)
        {
            var (seed, tick) = (Arg(0), Arg(2));
            if (!IsUniform(seed) || !IsUniform(tick))
                throw new Unsupported($"it draws {m.Name} with a seed or a tick that varies by row: a vector loop draws with one seed and one tick per run");
            source = $"{Scalar(seed)}, {Scalar(tick)}";
        }
        else if (call.Instance is { } random && IsUniform(random)) source = $"in {Scalar(random)}";
        else throw new Unsupported($"it draws from an EntityRandom that is not the method's: a vector loop draws from the 'in EntityRandom' parameter");

        if (Arg(id) is not IMemberReferenceOperation { Member.Name: "Id", Instance: { } owner } || !IsEntity(owner))
            throw FixableReason($"it draws {m.Name} for '{Text(Arg(id))}': a vector loop draws for the row's own entity, entity.Id of its Entity parameter");
        var stream = Arg(streamAt);
        if (!IsUniform(stream))
            throw FixableReason($"its draw's stream '{Text(stream)}' varies by row: give each call site a constant stream (0, 1, 2, ...)");
        // a flat loop's lanes are the fields of rows, not rows: the loop over rows draws
        if (_flat >= 0) throw new Unsupported("it draws EntityRandom numbers, one per row, in a loop over fields");
        string draw = $"{source}, in entities, {Row}, {Scalar(stream)}";
        _draws = true;
        var type = m.ReturnType;
        switch (m.Name)
        {
            case "Bits" when _size == 8:
                return $"global::EasyEcs.RandomLanes.Bits<{Vec("ulong")}>({draw})";
            case "Float" when _size == 4:
                return $"global::EasyEcs.RandomLanes.Float<{Vec("float")}>({draw})";
            case "Chance" when _size == 4:
            {
                // Float < probability, as the scalar member compares
                var single = m.Parameters[at].Type;
                return Compare("LessThan", $"global::EasyEcs.RandomLanes.Float<{Vec("float")}>({draw})", Expr(Arg(at)), single);
            }
            case "Range" when _size == 4 && IsFloat(type):
            {
                // min + Float × (max - min), the scalar member's two float operations per lane
                string min = Hold(Expr(Arg(at)), type), max = Expr(Arg(at + 1));
                return $"({min} + global::EasyEcs.RandomLanes.Float<{Vec("float")}>({draw}) * ({max} - {min}))";
            }
            case "Range" when _size == 4:
            {
                var (min, max) = (Arg(at), Arg(at + 1));
                if (!IsUniform(min) || !IsUniform(max))
                    throw new Unsupported($"it draws Range of ints between bounds that vary by row: a vector loop takes one range per run");
                string low = Scalar(min), high = Scalar(max);
                // max below min throws in the method: those rows run it (a constant pair is checked here)
                if (min.ConstantValue is { HasValue: true, Value: int a } && max.ConstantValue is { HasValue: true, Value: int b })
                {
                    if (b < a) throw new Unsupported($"it draws Range({a}, {b}), which throws: max is below min");
                }
                else Bail($"({high} < {low} ? {Vec(_mask)}.AllBitsSet : {Vec(_mask)}.Zero)");
                return $"global::EasyEcs.RandomLanes.Range<{Vec("int")}>({draw}, {low}, {high})";
            }
            default:
                throw new Unsupported($"it draws EntityRandom.{m.Name} ({type.ToDisplayString()}) in a loop over lanes of {_size} bytes: Float, Chance and Range draw 4-byte lanes, Bits 8-byte ones");
        }
    }
}
