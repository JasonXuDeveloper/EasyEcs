using EasyEcs;
using Godot;

namespace GodotSample;

// The scene's root node owns the world: it creates it in _Ready, steps it once per physics tick and draws what the
// systems computed. Game rules stay in the systems; the node only moves data between Godot and the world.
public partial class Main : Node2D
{
    const int Count = 1_000;

    World _world = null!;
    Rid _multimesh, _canvasItem;
    float[] _transforms = null!;
    int _ticks, _stopAfter;
    long _allocated;

    public override void _Ready()
    {
        _world = new World();
        _world.SetArena(Size: new Vector2(640, 360));
        // Godot's physics ticks have a fixed length (Physics > Common > Physics Ticks per Second).
        _world.SetStep(Seconds: (float)GetPhysicsProcessDeltaTime());
        _world.AddSystem<MoveSystem>();
        _world.AddSystem<BounceSystem>();

        // A fixed seed, so every run (and CI) sees the same entities.
        var random = new System.Random(7);
        _world.Reserve<Position, Velocity>(Count);
        for (int i = 0; i < Count; i++)
        {
            var at = new Vector2(random.Next(640), random.Next(360));
            var speed = new Vector2(random.Next(-120, 121), random.Next(-120, 121));
            _world.CreateEntity(new Position(at), new Velocity(speed));
        }

        CreateMultimesh();
        _stopAfter = Support.TicksFromCommandLine();
    }

    public override void _PhysicsProcess(double delta)
    {
        // The first update builds the queries' caches; every later one allocates nothing.
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        _world.Update();
        if (_ticks > 0) _allocated += System.GC.GetAllocatedBytesForCurrentThread() - before;
        Render();

        if (++_ticks == _stopAfter)
        {
            Support.Report(_world, _ticks, _allocated);
            GetTree().Quit();
        }
    }

    public override void _ExitTree()
    {
        _world.Dispose();
        RenderingServer.FreeRid(_canvasItem);
        RenderingServer.FreeRid(_multimesh);
    }

    // One multimesh draws every entity in one call. Each instance takes 8 floats of the buffer
    // (x.x, y.x, 0, origin.x, x.y, y.y, 0, origin.y), so only the origins change per tick.
    void CreateMultimesh()
    {
        var quad = new QuadMesh { Size = new Vector2(4, 4) };
        _multimesh = RenderingServer.MultimeshCreate();
        RenderingServer.MultimeshSetMesh(_multimesh, quad.GetRid());
        RenderingServer.MultimeshAllocateData(_multimesh, Count, RenderingServer.MultimeshTransformFormat.Transform2D);
        _transforms = new float[Count * 8];
        for (int i = 0; i < Count; i++)
        {
            _transforms[i * 8] = 1;
            _transforms[i * 8 + 5] = 1;
        }
        _canvasItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_canvasItem, GetCanvasItem());
        RenderingServer.CanvasItemAddMultimesh(_canvasItem, _multimesh);
    }

    // Reads the positions the systems wrote and hands them to the renderer.
    void Render()
    {
        int i = 0;
        foreach (var item in _world.Query<Position>())
        {
            _transforms[i * 8 + 3] = item.Position.Value.X;
            _transforms[i * 8 + 7] = item.Position.Value.Y;
            i++;
        }
        RenderingServer.MultimeshSetBuffer(_multimesh, _transforms);
    }
}
