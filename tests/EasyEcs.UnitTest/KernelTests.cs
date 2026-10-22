using System;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct Heat : IComponent { public float Value; }
public struct Speed : IComponent { public float Value; }
public struct Mass : IComponent { public float Value; }
public record struct Bits(int Value) : IComponent;     // a positional record struct: its lane is an auto-property
public record struct Mask(int Value) : IComponent;
public record struct Gravity(float Pull) : ISingletonComponent;

/// <summary>Kernels written for one row; the generator runs them as Vector&lt;T&gt; loops (Kernel.cs).</summary>
public static class Kernels
{
    public const float Limit = 10f;
    public static readonly float Drag = 0.25f;

    [ForEach]
    internal static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Gravity gravity)
    {
        float force = heat.Value * 0.5f - mass.Value * gravity.Pull;
        speed.Value = MathF.Max(MathF.Min(speed.Value + force - Drag, Limit), -Limit);
        if (heat.Value > 100f) heat.Value = 100f;
        else heat.Value += speed.Value * 0.125f;
    }

    [ForEach]
    internal static void Guard(ref Heat heat, in Mass mass, float scale)
    {
        if (mass.Value <= 0 || mass.Value != mass.Value) return; // NaN: a return clears its lanes
        heat.Value = mass.Value > 1 ? MathF.Sqrt(mass.Value) * scale : heat.Value / mass.Value;
        heat.Value -= 1;
    }

    [ForEach]
    internal static void Shift(ref Bits bits, in Mask mask)
    {
        var mixed = (bits.Value << 3) ^ (bits.Value >> 2) ^ ~mask.Value;
        bits.Value = mixed > 0 && (mask.Value & 1) == 0 ? mixed * 7 - 3 : Math.Max(mixed, mask.Value);
        bits.Value++;
    }
}

public class KernelTests
{
    // Lane counts that leave a tail, and two tables (the Frozen tag) so a pass sees several runs.
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(1003)]
    public void VectorLoopsMatchTheMethodBitForBit(int count)
    {
        float[] special = [float.NaN, -0f, 0f, 1f, -1f, 100f, 101f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f];
        var random = new Random(count);
        float Next(int i) => i % 5 == 0 ? special[random.Next(special.Length)] : (float)(random.NextDouble() * 300 - 100);

        using var world = new World();
        world.SetGravity(0.75f);
        var heat = new Heat[count];
        var speed = new Speed[count];
        var mass = new Mass[count];
        var bits = new Bits[count];
        var masks = new Mask[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            heat[i].Value = Next(i);
            speed[i].Value = Next(i + 1);
            mass[i].Value = Next(i + 2);
            bits[i] = new Bits(random.Next(int.MinValue, int.MaxValue));
            masks[i] = new Mask(random.Next(int.MinValue, int.MaxValue));
            entities[i] = world.CreateEntity(heat[i], speed[i], mass[i], bits[i], masks[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        var gravity = world.Gravity;
        for (int round = 0; round < 3; round++)
        {
            Kernels.Step(world);
            Kernels.Guard(world, 1.5f);
            Kernels.Shift(world);
            for (int i = 0; i < count; i++)
            {
                Kernels.Step(ref heat[i], ref speed[i], in mass[i], in gravity);
                Kernels.Guard(ref heat[i], in mass[i], 1.5f);
                Kernels.Shift(ref bits[i], in masks[i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Heat.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(heat[i].Value)), $"heat {i}");
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Speed.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(speed[i].Value)), $"speed {i}");
            Assert.That(entities[i].Bits, Is.EqualTo(bits[i]), $"bits {i}");
        }
    }
}
