using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Sessions holding an outside resource: 10,000 connections; per operation, 1,000 disconnect and 1,000 connect. With a
/// cleanup component (<c>Socket : ICleanupComponent</c>: a retired session keeps it with the <c>Retired</c> tag
/// until a [ForEach] pass releases it) against the same churn with a plain component released before the destroy.
/// Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class CleanupBenchmark
{
    [Params(10_000)] public int Sessions;

    private World _cleanup, _plain;
    private Entity[] _cleanupSessions, _plainSessions;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _cleanup = new World(new WorldOptions { InitialEntityCapacity = Sessions * 2 });
        _plain = new World(new WorldOptions { InitialEntityCapacity = Sessions * 2 });
        _cleanupSessions = new Entity[Sessions];
        _plainSessions = new Entity[Sessions];
        _cleanup.CreateEntities(_cleanupSessions, new Inbox(0), new Socket(1));
        _plain.CreateEntities(_plainSessions, new Inbox(0), new PlainSocket(1));
        for (int i = 0; i < 10; i++)
        {
            Cleanup();
            Plain();
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _cleanup.Dispose();
        _plain.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Cleanup()
    {
        for (int i = 0; i < 1_000; i++) _cleanupSessions[Pick(i)].Destroy();      // Socket stays behind
        int released = 0;
        Sockets.Release(_cleanup, ref released);                                // one pass over what was left
        for (int i = 0; i < 1_000; i++) _cleanupSessions[Pick(i)] = _cleanup.CreateEntity(new Inbox(0), new Socket(i));
        _next++;
        return released;
    }

    [Benchmark]
    public int Plain()
    {
        int released = 0;
        for (int i = 0; i < 1_000; i++)
        {
            var session = _plainSessions[Pick(i)];
            released += session.Read<PlainSocket>().Handle & 1;                // released by the code that destroys
            session.Destroy();
        }

        for (int i = 0; i < 1_000; i++) _plainSessions[Pick(i)] = _plain.CreateEntity(new Inbox(0), new PlainSocket(i));
        _next++;
        return released;
    }

    private int Pick(int i) => (_next * 1_000 + i) % Sessions;

    public record struct Inbox(int Count) : IComponent;
    public record struct Socket(int Handle) : ICleanupComponent;
    public record struct PlainSocket(int Handle) : IComponent;
}

public static class Sockets
{
    [ForEach, With<Retired>]
    static void Release(Entity session, in CleanupBenchmark.Socket socket, ref int released)
    {
        released += socket.Handle & 1;
        session.Remove<CleanupBenchmark.Socket>();                               // the last one: the entity is freed
    }
}
