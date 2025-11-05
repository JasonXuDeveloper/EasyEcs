using EasyEcs.Core.Components;
using System.Threading;

namespace EasyEcs.Core;

/// <summary>
/// Thread-safe singleton storage with volatile fields for cross-thread visibility.
/// Write access should be synchronized (e.g., via Context._structuralLock).
/// </summary>
public class Singleton<T> where T : struct, ISingletonComponent
{
    // Volatile ensures visibility across threads
    public volatile bool Initialized = false;
    public T Value;
    public static Singleton<T> Instance = new();
}