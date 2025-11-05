using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core;

/// <summary>
/// Zero-allocation priority-based system list for high-performance iteration.
/// Systems are grouped by priority and iterated in ascending priority order.
/// </summary>
internal class PrioritySystemList<T>
{
    private struct PriorityBucket
    {
        public int Priority;
        public List<T> Systems;

        public PriorityBucket(int priority)
        {
            Priority = priority;
            Systems = new List<T>();
        }
    }

    private readonly List<PriorityBucket> _buckets = new();

    /// <summary>
    /// Add a system with the given priority. Systems are automatically sorted by priority.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int priority, T system)
    {
        // Find or create bucket for this priority
        int bucketIndex = -1;
        for (int i = 0; i < _buckets.Count; i++)
        {
            if (_buckets[i].Priority == priority)
            {
                bucketIndex = i;
                break;
            }
            else if (_buckets[i].Priority > priority)
            {
                // Insert new bucket at this position to maintain sorted order
                bucketIndex = i;
                _buckets.Insert(i, new PriorityBucket(priority));
                break;
            }
        }

        // If not found, append at the end
        if (bucketIndex == -1)
        {
            bucketIndex = _buckets.Count;
            _buckets.Add(new PriorityBucket(priority));
        }

        // Add system to bucket
        _buckets[bucketIndex].Systems.Add(system);
    }

    /// <summary>
    /// Get a system list at the specified bucket index (zero allocation).
    /// </summary>
    public List<T> this[int bucketIndex]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buckets[bucketIndex].Systems;
    }

    /// <summary>
    /// Get the count of priority buckets.
    /// </summary>
    public int BucketCount => _buckets.Count;

    /// <summary>
    /// Clear all systems.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _buckets.Clear();
    }
}
