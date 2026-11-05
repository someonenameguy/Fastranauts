using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fastranauts.Common
{
    /// <summary>
    /// Zero-allocation drop-in replacement for Physics.RaycastAll and subsequent OrderBy(hit => hit.distance).
    /// Uses Physics.RaycastNonAlloc into a static scratch buffer, performs an in-place insertion sort by distance,
    /// and returns a pre-sized RaycastHit[] from a static table so callers see the exact hit count with 0 GC allocations.
    /// </summary>
    public static class FastRaycast
    {
        public const int MaxHits = 64;

        private static readonly RaycastHit[] _scratchBuffer = new RaycastHit[MaxHits];
        private static readonly RaycastHit[][] _arraysByLength = new RaycastHit[MaxHits + 1][];
        public static readonly ZeroAllocOrderedHitEnumerable SharedOrderedEnumerable = new ZeroAllocOrderedHitEnumerable();

        static FastRaycast()
        {
            for (int i = 0; i <= MaxHits; i++)
            {
                _arraysByLength[i] = new RaycastHit[i];
            }
        }

        public static RaycastHit[] RaycastAll(Ray ray, float maxDistance)
        {
            FastStats.NonAllocRaycastsExecuted++;
            int count = Physics.RaycastNonAlloc(ray, _scratchBuffer, maxDistance);
            return FinalizeHits(count);
        }

        public static RaycastHit[] RaycastAll(Ray ray, float maxDistance, int layerMask)
        {
            FastStats.NonAllocRaycastsExecuted++;
            int count = Physics.RaycastNonAlloc(ray, _scratchBuffer, maxDistance, layerMask);
            return FinalizeHits(count);
        }

        private static RaycastHit[] FinalizeHits(int count)
        {
            if (count <= 0)
            {
                SharedOrderedEnumerable.SetData(_arraysByLength[0], 0);
                return _arraysByLength[0];
            }

            if (count > MaxHits)
            {
                count = MaxHits;
            }

            // In-place insertion sort by distance (fastest and zero-alloc for small N <= 64)
            for (int i = 1; i < count; i++)
            {
                RaycastHit key = _scratchBuffer[i];
                float keyDist = key.distance;
                int j = i - 1;
                while (j >= 0 && _scratchBuffer[j].distance > keyDist)
                {
                    _scratchBuffer[j + 1] = _scratchBuffer[j];
                    j--;
                }
                _scratchBuffer[j + 1] = key;
            }

            RaycastHit[] result = _arraysByLength[count];
            Array.Copy(_scratchBuffer, 0, result, 0, count);
            SharedOrderedEnumerable.SetData(result, count);
            return result;
        }

        /// <summary>
        /// Replaces Enumerable.OrderBy&lt;RaycastHit, float&gt;(enumerable, go =&gt; go.distance)
        /// when enumerable was produced by FastRaycast.RaycastAll (already sorted by distance).
        /// </summary>
        public static IOrderedEnumerable<RaycastHit> PassThroughOrderBy(
            IEnumerable<RaycastHit> source,
            Func<RaycastHit, float> keySelector)
        {
            if (source is RaycastHit[] arr)
            {
                SharedOrderedEnumerable.SetData(arr, arr.Length);
                return SharedOrderedEnumerable;
            }
            return Enumerable.OrderBy(source, keySelector);
        }

        /// <summary>
        /// Replaces Enumerable.ToArray&lt;RaycastHit&gt;(orderedEnumerable) with zero allocation.
        /// </summary>
        public static RaycastHit[] ToArrayZeroAlloc(IEnumerable<RaycastHit> source)
        {
            if (ReferenceEquals(source, SharedOrderedEnumerable))
            {
                return SharedOrderedEnumerable.BackingArray;
            }
            if (source is RaycastHit[] arr)
            {
                return arr;
            }
            return Enumerable.ToArray(source);
        }

        /// <summary>
        /// Reusable singleton implementing both IOrderedEnumerable&lt;RaycastHit&gt; and IEnumerator&lt;RaycastHit&gt;
        /// so foreach loops over IEnumerable&lt;RaycastHit&gt; in CrewSim.GetMouseOverCO allocate 0 bytes.
        /// </summary>
        public sealed class ZeroAllocOrderedHitEnumerable : IOrderedEnumerable<RaycastHit>, IEnumerator<RaycastHit>
        {
            private RaycastHit[] _items = _arraysByLength[0];
            private int _count;
            private int _index = -1;

            public RaycastHit[] BackingArray => _items;

            public void SetData(RaycastHit[] items, int count)
            {
                _items = items ?? _arraysByLength[0];
                _count = count;
                _index = -1;
            }

            public IOrderedEnumerable<RaycastHit> CreateOrderedEnumerable<TKey>(
                Func<RaycastHit, TKey> keySelector,
                IComparer<TKey> comparer,
                bool descending)
            {
                return this;
            }

            public IEnumerator<RaycastHit> GetEnumerator()
            {
                _index = -1;
                return this;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                _index = -1;
                return this;
            }

            public RaycastHit Current => _items[_index];

            object IEnumerator.Current => _items[_index];

            public bool MoveNext()
            {
                int next = _index + 1;
                if (next < _count)
                {
                    _index = next;
                    return true;
                }
                return false;
            }

            public void Reset()
            {
                _index = -1;
            }

            public void Dispose()
            {
                _index = -1;
            }
        }
    }
}
