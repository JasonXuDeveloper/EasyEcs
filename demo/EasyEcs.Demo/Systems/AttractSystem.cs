using EasyEcs;

namespace EasyEcs.Demo;

// Pulls the sparks towards the pointer while it is over the canvas, and swirls them around it.
[EcsSystem]
public ref struct AttractSystem
{
    // Pull and swirl, in pixels per second squared at the softening distance.
    private const float Pull = 1800f;
    private const float Swirl = 1400f;

    // Pixels within which the force stops growing, so sparks pass through the pointer smoothly.
    private const float Soft = 40f;

    [ForEach]
    static void Attract(ref Velocity v, in Position p, in Pointer pointer, in Clock clock)
    {
        if (!pointer.Active)
        {
            return;
        }

        float dx = pointer.X - p.X;
        float dy = pointer.Y - p.Y;

        // Falls off with distance (1 / r): near sparks orbit, far ones drift in
        float scale = clock.Dt * Soft / (dx * dx + dy * dy + Soft * Soft);
        v.X += (dx * Pull - dy * Swirl) * scale;
        v.Y += (dy * Pull + dx * Swirl) * scale;
    }
}
