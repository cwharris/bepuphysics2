﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuPhysics.Constraints.Contact;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static BepuPhysics.CollisionDetection.WorkerPairCache;

namespace BepuPhysics.CollisionDetection
{
    //would you care for some generics
    using OverlapMapping = QuickDictionary<CollidablePair, CollidablePairPointers, Buffer<CollidablePair>, Buffer<CollidablePairPointers>, Buffer<int>, CollidablePairComparer>;

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct CollidablePair
    {
        [FieldOffset(0)]
        public CollidableReference A;
        [FieldOffset(4)]
        public CollidableReference B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CollidablePair(CollidableReference a, CollidableReference b)
        {
            A = a;
            B = b;
        }

        public override string ToString()
        {
            return $"<{A.Mobility}[{A.Handle}], {B.Mobility}[{B.Handle}]>";
        }
    }

    public struct CollidablePairComparer : IEqualityComparerRef<CollidablePair>
    {
        //Note that pairs are sorted by handle, so we can assume order matters.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ref CollidablePair a, ref CollidablePair b)
        {
            return Unsafe.As<CollidablePair, ulong>(ref a) == Unsafe.As<CollidablePair, ulong>(ref b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Hash(ref CollidablePair item)
        {
            const ulong p1 = 961748927UL;
            const ulong p2 = 899809343UL;
            var hash64 = (ulong)item.A.Packed * (p1 * p2) + (ulong)item.B.Packed * (p2);
            return (int)(hash64 ^ (hash64 >> 32));
        }
    }

    public struct CollidablePairPointers
    {
        /// <summary>
        /// A narrowphase-specific type and index into the pair cache's constraint data set. Collision pairs which have no associated constraint, either 
        /// because no contacts were generated or because the constraint was filtered, will have a nonexistent ConstraintCache.
        /// </summary>
        public PairCacheIndex ConstraintCache;
        /// <summary>
        /// A narrowphase-specific type and index into a batch of custom data for the pair. Many types do not use any supplementary data, but some make use of temporal coherence
        /// to accelerate contact generation.
        /// </summary>
        public PairCacheIndex CollisionDetectionCache;
    }


    public partial class PairCache
    {
        public OverlapMapping Mapping;

        /// <summary>
        /// Per-pair 'freshness' flags set when a pair is added or updated by the narrow phase execution. Only initialized for the duration of the narrowphase's execution.
        /// </summary>
        /// <remarks>
        /// This stores one byte per pair. While it could be compressed to 1 bit, that requires manually ensuring thread safety. By using bytes, we rely on the 
        /// atomic setting behavior for data types no larger than the native pointer size. Further, smaller sizes actually pay a higher price in terms of increased false sharing.
        /// Choice of data type is a balancing act between the memory bandwidth of the post analysis and the frequency of false sharing.
        /// </remarks>
        internal RawBuffer PairFreshness;
        BufferPool pool;
        int minimumPendingSize;
        int minimumPerTypeCapacity;
        int previousPendingSize;

        //While the current worker caches are read from, the next caches are written to.
        //The worker pair caches contain a reference to a buffer pool, which is a reference type. That makes WorkerPairCache non-blittable, so in the interest of not being
        //super duper gross, we don't use the untyped buffer pools to store it. 
        //Given that the size of the arrays here will be small and almost never change, this isn't a significant issue.
        QuickList<WorkerPairCache, Array<WorkerPairCache>> workerCaches;
        internal QuickList<WorkerPairCache, Array<WorkerPairCache>> NextWorkerCaches;


        public PairCache(BufferPool pool, int initialSetCapacity, int minimumMappingSize, int minimumPendingSize, int minimumPerTypeCapacity)
        {
            this.minimumPendingSize = minimumPendingSize;
            this.minimumPerTypeCapacity = minimumPerTypeCapacity;
            this.pool = pool;
            OverlapMapping.Create(
                pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>(),
                SpanHelper.GetContainingPowerOf2(minimumMappingSize), 3, out Mapping);
            ResizeSetsCapacity(initialSetCapacity, 0);
        }

        public void Prepare(IThreadDispatcher threadDispatcher = null)
        {
            int maximumConstraintTypeCount = 0, maximumCollisionTypeCount = 0;
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].GetMaximumCacheTypeCounts(out var collision, out var constraint);
                if (collision > maximumCollisionTypeCount)
                    maximumCollisionTypeCount = collision;
                if (constraint > maximumConstraintTypeCount)
                    maximumConstraintTypeCount = constraint;
            }
            QuickList<PreallocationSizes, Buffer<PreallocationSizes>>.Create(pool.SpecializeFor<PreallocationSizes>(), maximumConstraintTypeCount, out var minimumSizesPerConstraintType);
            QuickList<PreallocationSizes, Buffer<PreallocationSizes>>.Create(pool.SpecializeFor<PreallocationSizes>(), maximumCollisionTypeCount, out var minimumSizesPerCollisionType);
            //Since the minimum size accumulation builds the minimum size incrementally, bad data within the array can corrupt the result- we must clear it.
            minimumSizesPerConstraintType.Span.Clear(0, minimumSizesPerConstraintType.Span.Length);
            minimumSizesPerCollisionType.Span.Clear(0, minimumSizesPerCollisionType.Span.Length);
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].AccumulateMinimumSizes(ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType);
            }

            var threadCount = threadDispatcher != null ? threadDispatcher.ThreadCount : 1;
            //Ensure that the new worker pair caches can hold all workers.
            if (!NextWorkerCaches.Span.Allocated || NextWorkerCaches.Span.Length < threadCount)
            {
                //The next worker caches should never need to be disposed here. The flush should have taken care of it.
#if DEBUG
                for (int i = 0; i < NextWorkerCaches.Count; ++i)
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)));
#endif
                QuickList<WorkerPairCache, Array<WorkerPairCache>>.Create(new PassthroughArrayPool<WorkerPairCache>(), threadCount, out NextWorkerCaches);
            }
            //Note that we have not initialized the workerCaches from the previous frame. In the event that this is the first frame and there are no previous worker caches,
            //there will be no pointers into the caches, and removal analysis loops over the count which defaults to zero- so it's safe.
            NextWorkerCaches.Count = threadCount;

            var pendingSize = Math.Max(minimumPendingSize, previousPendingSize);
            if (threadDispatcher != null)
            {
                for (int i = 0; i < threadCount; ++i)
                {
                    NextWorkerCaches[i] = new WorkerPairCache(i, threadDispatcher.GetThreadMemoryPool(i), ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType,
                        pendingSize, minimumPerTypeCapacity);
                }
            }
            else
            {
                NextWorkerCaches[0] = new WorkerPairCache(0, pool, ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType, pendingSize, minimumPerTypeCapacity);
            }
            minimumSizesPerConstraintType.Dispose(pool.SpecializeFor<PreallocationSizes>());
            minimumSizesPerCollisionType.Dispose(pool.SpecializeFor<PreallocationSizes>());

            //Create the pair freshness array for the existing overlaps.
            pool.Take(Mapping.Count, out PairFreshness);
            //This clears 1 byte per pair. 32768 pairs with 10GBps assumed single core bandwidth means about 3 microseconds.
            //There is a small chance that multithreading this would be useful in larger simulations- but it would be very, very close.
            PairFreshness.Clear(0, Mapping.Count);

        }


        internal void EnsureConstraintToPairMappingCapacity(Solver solver, int targetCapacity)
        {
            targetCapacity = Math.Max(solver.HandlePool.HighestPossiblyClaimedId + 1, targetCapacity);
            if (ConstraintHandleToPair.Length < targetCapacity)
            {
                pool.SpecializeFor<CollisionPairLocation>().Resize(ref ConstraintHandleToPair, targetCapacity, ConstraintHandleToPair.Length);
            }
        }

        internal void ResizeConstraintToPairMappingCapacity(Solver solver, int targetCapacity)
        {
            targetCapacity = BufferPool<CollisionPairLocation>.GetLowestContainingElementCount(Math.Max(solver.HandlePool.HighestPossiblyClaimedId + 1, targetCapacity));
            if (ConstraintHandleToPair.Length != targetCapacity)
            {
                pool.SpecializeFor<CollisionPairLocation>().Resize(ref ConstraintHandleToPair, targetCapacity, Math.Min(targetCapacity, ConstraintHandleToPair.Length));
            }
        }



        /// <summary>
        /// Flush all deferred changes from the last narrow phase execution.
        /// </summary>
        public void PrepareFlushJobs(ref QuickList<NarrowPhaseFlushJob, Buffer<NarrowPhaseFlushJob>> jobs)
        {
            //Get rid of the now-unused worker caches.
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }

            //The freshness cache should have already been used in order to generate the constraint removal requests and the PendingRemoves that we handle in a moment; dispose it now.
            pool.Return(ref PairFreshness);

            //Ensure the overlap mapping size is sufficient up front. This requires scanning all the pending sizes.
            int largestIntermediateSize = Mapping.Count;
            var newMappingSize = Mapping.Count;
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];
                //Removes occur first, so this cache can only result in a larger mapping if there are more adds than removes.
                newMappingSize += cache.PendingAdds.Count - cache.PendingRemoves.Count;
                if (newMappingSize > largestIntermediateSize)
                    largestIntermediateSize = newMappingSize;
            }
            Mapping.EnsureCapacity(largestIntermediateSize, pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>());

            jobs.Add(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.FlushPairCacheChanges }, pool.SpecializeFor<NarrowPhaseFlushJob>());
        }
        public unsafe void FlushMappingChanges()
        {
            //Flush all pending adds from the new set.
            //Note that this phase accesses no shared memory- it's all pair cache local, and no pool accesses are made.
            //That means we could run it as a job alongside solver constraint removal. That's good, because adding and removing to the hash tables isn't terribly fast.  
            //(On the order of 10-100 nanoseconds per operation, so in pathological cases, it can start showing up in profiles.)
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];

                //Walk backwards on the off chance that a swap can be avoided.
                for (int j = cache.PendingRemoves.Count - 1; j >= 0; --j)
                {
                    var removed = Mapping.FastRemove(ref cache.PendingRemoves[j]);
                    Debug.Assert(removed);
                }
                for (int j = 0; j < cache.PendingAdds.Count; ++j)
                {
                    ref var pending = ref cache.PendingAdds[j];
                    var added = Mapping.AddUnsafely(ref pending.Pair, ref pending.Pointers);
                    Debug.Assert(added);
                }
            }
        }
        public void Postflush()
        {
            //This bookkeeping and disposal phase is trivially cheap compared to the cost of updating the mapping table, so we do it sequentially.
            //The fact that we access the per-worker pools here would prevent easy multithreading anyway; the other threads may use them. 
            int largestPendingSize = 0;
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];
                if (cache.PendingAdds.Count > largestPendingSize)
                {
                    largestPendingSize = cache.PendingAdds.Count;
                }
                if (cache.PendingRemoves.Count > largestPendingSize)
                {
                    largestPendingSize = cache.PendingRemoves.Count;
                }
                cache.PendingAdds.Dispose(cache.pool.SpecializeFor<PendingAdd>());
                cache.PendingRemoves.Dispose(cache.pool.SpecializeFor<CollidablePair>());
            }
            previousPendingSize = largestPendingSize;

            //Swap references.
            var temp = workerCaches;
            workerCaches = NextWorkerCaches;
            NextWorkerCaches = temp;


        }

        internal void Clear()
        {
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }
            workerCaches.Count = 0;
            for (int i = 1; i < SleepingSets.Length; ++i)
            {
                if (SleepingSets[i].Allocated)
                {
                    SleepingSets[i].Dispose(pool);
                }
            }
#if DEBUG
            if (NextWorkerCaches.Span.Allocated)
            {
                for (int i = 0; i < NextWorkerCaches.Count; ++i)
                {
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)), "Outside of the execution of the narrow phase, the 'next' caches should not be allocated.");
                }
            }
#endif
        }

        public void Dispose()
        {
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }
            //Note that we do not need to dispose the worker cache arrays themselves- they were just arrays pulled out of a passthrough pool.
#if DEBUG
            if (NextWorkerCaches.Span.Allocated)
            {
                for (int i = 0; i < NextWorkerCaches.Count; ++i)
                {
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)), "Outside of the execution of the narrow phase, the 'next' caches should not be allocated.");
                }
            }
#endif
            Mapping.Dispose(pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>());
            for (int i = 1; i < SleepingSets.Length; ++i)
            {
                ref var set = ref SleepingSets[i];
                if (set.Allocated)
                    set.Dispose(pool);
            }
            pool.SpecializeFor<SleepingSet>().Return(ref SleepingSets);
            //The constraint handle to pair is partially slaved to the constraint handle capacity. 
            //It gets ensured every frame, but the gap between construction and the first frame could leave it uninitialized.
            if (ConstraintHandleToPair.Allocated)
                pool.SpecializeFor<CollisionPairLocation>().Return(ref ConstraintHandleToPair);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(ref CollidablePair pair)
        {
            return Mapping.IndexOf(ref pair);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref CollidablePairPointers GetPointers(int index)
        {
            return ref Mapping.Values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void FillNewConstraintCache<TConstraintCache>(int* featureIds, ref TConstraintCache cache)
        {
            //1 contact constraint caches do not store a feature id; it's pointless.
            if (typeof(TConstraintCache) == typeof(ConstraintCache2))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache2>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
            }
            else if (typeof(TConstraintCache) == typeof(ConstraintCache3))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache3>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
                typedCache.FeatureId2 = featureIds[2];
            }
            else if (typeof(TConstraintCache) == typeof(ConstraintCache4))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache4>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
                typedCache.FeatureId2 = featureIds[2];
                typedCache.FeatureId3 = featureIds[3];
            }
            //TODO: In the event that higher contact count manifolds exist for the purposes of nonconvexes, this will need to be expanded.
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe PairCacheIndex Add<TConstraintCache, TCollisionCache>(int workerIndex, ref CollidablePair pair,
            ref TCollisionCache collisionCache, ref TConstraintCache constraintCache)
            where TConstraintCache : IPairCacheEntry
            where TCollisionCache : IPairCacheEntry
        {
            //Note that we do not have to set any freshness bytes here; using this path means there exists no previous overlap to remove anyway.
            return NextWorkerCaches[workerIndex].Add(ref pair, ref collisionCache, ref constraintCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void Update<TConstraintCache, TCollisionCache>(int workerIndex, int pairIndex, ref CollidablePairPointers pointers,
            ref TCollisionCache collisionCache, ref TConstraintCache constraintCache)
            where TConstraintCache : IPairCacheEntry
            where TCollisionCache : IPairCacheEntry
        {
            //We're updating an existing pair, so we should prevent this pair from being removed.
            PairFreshness[pairIndex] = 0xFF;
            NextWorkerCaches[workerIndex].Update(ref pointers, ref collisionCache, ref constraintCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetContactCount(int constraintType)
        {
            //TODO: Very likely that we'll expand the nonconvex manifold maximum to 8 contacts, so this will need to be adjusted later.
            return 1 + (constraintType & 0x3);
        }

        /// <summary>
        /// Gets whether a constraint type id maps to a contact constraint.
        /// </summary>
        /// <param name="constraintTypeId">Id of the constraint to check.</param>
        /// <returns>True if the type id refers to a contact constraint. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsContactBatch(int constraintTypeId)
        {
            //TODO: If the nonconvex contact count expands to 8, this will have to change.
            return constraintTypeId < 16;
        }
        
        //TODO: If we add in nonconvex manifolds with up to 8 contacts, this will need to change- we preallocate enough space to hold all possible narrowphase generated types.
        public const int CollisionConstraintTypeCount = 16;
        public const int CollisionTypeCount = 16;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void* GetOldConstraintCachePointer(int pairIndex)
        {
            ref var constraintCacheIndex = ref Mapping.Values[pairIndex].ConstraintCache;
            return workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe int GetOldConstraintHandle(int pairIndex)
        {
            ref var constraintCacheIndex = ref Mapping.Values[pairIndex].ConstraintCache;
            return *(int*)workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex);
        }

        /// <summary>
        /// Completes the addition of a constraint by filling in the narrowphase's pointer to the constraint and by distributing accumulated impulses.
        /// </summary>
        /// <typeparam name="TContactImpulses">Count-specialized type containing cached accumulated impulses.</typeparam>
        /// <param name="narrowPhase">Narrow phase that triggered the constraint add.</param>
        /// <param name="solver">Solver containing the constraint to set the impulses of.</param>
        /// <param name="impulses">Warm starting impulses to apply to the contact constraint.</param>
        /// <param name="constraintCacheIndex">Index of the constraint cache to update.</param>
        /// <param name="constraintHandle">Constraint handle associated with the constraint cache being updated.</param>
        /// <param name="pair">Collidable pair associated with the new constraint.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void CompleteConstraintAdd<TContactImpulses>(NarrowPhase narrowPhase, Solver solver, ref TContactImpulses impulses, PairCacheIndex constraintCacheIndex,
            int constraintHandle, ref CollidablePair pair)
        {
            //Note that the update is being directed to the *next* worker caches. We have not yet performed the flush that swaps references.
            //Note that this assumes that the constraint handle is stored in the first 4 bytes of the constraint cache.
            *(int*)NextWorkerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex) = constraintHandle;
            solver.GetConstraintReference(constraintHandle, out var reference);
            narrowPhase.contactConstraintAccessors[constraintCacheIndex.Type].ScatterNewImpulses(ref reference, ref impulses);
            //This mapping entry had to be deferred until now because no constraint handle was known until now. Now that we have it,
            //we can fill in the pointers back to the overlap mapping.
            ConstraintHandleToPair[constraintHandle].Pair = pair;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref TConstraintCache GetConstraintCache<TConstraintCache>(PairCacheIndex constraintCacheIndex)
        {
            //Note that these refer to the previous workerCaches, not the nextWorkerCaches. We read from these caches during the narrowphase to redistribute impulses.
            return ref Unsafe.AsRef<TConstraintCache>(workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref TCollisionData GetCollisionData<TCollisionData>(PairCacheIndex index) where TCollisionData : struct, IPairCacheEntry
        {
            return ref Unsafe.AsRef<TCollisionData>(workerCaches[index.Cache].GetCollisionCachePointer(index));
        }

    }
}
