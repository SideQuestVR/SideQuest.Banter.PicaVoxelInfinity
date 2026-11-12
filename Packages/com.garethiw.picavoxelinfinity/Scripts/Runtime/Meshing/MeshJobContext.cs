/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace PicaVoxel
{
    /// <summary>
    /// The buffers one <see cref="CulledMeshJob"/> works in, kept between jobs so meshing allocates nothing.
    /// A volume pools a few of these; a chunk holds one from scheduling until its results are applied.
    /// </summary>
    public sealed unsafe class MeshJobContext : IDisposable
    {
        public readonly int ChunkSize;

        public NativeArray<Voxel> Padded;
        public NativeArray<uint> FaceMasks;
        public NativeArray<long> Neighbours;
        public NativeList<ChunkVertex> Vertices;
        public NativeList<int> Indices;
        public NativeList<ushort> Indices16;
        public NativeList<float3> CollisionVertices;
        public NativeList<int> CollisionIndices;
        public NativeArray<float3> Bounds;

        public JobHandle Handle;
        public bool Scheduled;
        // The chunk generation the job was started for; results for a recycled chunk are thrown away
        public int Generation;

        readonly ulong[] _pins = new ulong[27];
        readonly bool[] _pinned = new bool[27];

        public MeshJobContext(int chunkSize)
        {
            ChunkSize = chunkSize;
            int padded = (chunkSize + 2) * (chunkSize + 2) * (chunkSize + 2);
            Padded = new NativeArray<Voxel>(padded, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            FaceMasks = new NativeArray<uint>(12 * chunkSize * chunkSize, Allocator.Persistent);
            Neighbours = new NativeArray<long>(27, Allocator.Persistent);
            Vertices = new NativeList<ChunkVertex>(4096, Allocator.Persistent);
            Indices = new NativeList<int>(6144, Allocator.Persistent);
            Indices16 = new NativeList<ushort>(6144, Allocator.Persistent);
            CollisionVertices = new NativeList<float3>(1024, Allocator.Persistent);
            CollisionIndices = new NativeList<int>(1536, Allocator.Persistent);
            Bounds = new NativeArray<float3>(2, Allocator.Persistent);
        }

        /// <summary>
        /// Pins the voxel arrays of the chunk and its neighbours (27, centre at 13; null for none) for the job to read.
        /// Main thread only. Released by <see cref="Complete"/>.
        /// </summary>
        public void Pin(Chunk[] neighbours)
        {
            int voxels = ChunkSize * ChunkSize * ChunkSize;
            for (int i = 0; i < 27; i++)
            {
                Voxel[] arr = neighbours[i] != null ? neighbours[i].Voxels : null;
                if (arr == null || arr.Length < voxels)
                {
                    Neighbours[i] = 0;
                    continue;
                }
                Neighbours[i] = (long)UnsafeUtility.PinGCArrayAndGetDataAddress(arr, out _pins[i]);
                _pinned[i] = true;
            }
        }

        void Unpin()
        {
            for (int i = 0; i < 27; i++)
            {
                if (!_pinned[i]) continue;
                UnsafeUtility.ReleaseGCObject(_pins[i]);
                _pinned[i] = false;
            }
        }

        public CulledMeshJob CreateJob(Volume volume, bool collision)
        {
            return new CulledMeshJob
            {
                ChunkSize = ChunkSize,
                VoxelSize = volume.VoxelSize,
                SelfShadeIntensity = volume.SelfShadingIntensity,
                BuildCollision = collision,
                MergeCollision = volume.MergeColliderFaces,
                Neighbours = Neighbours,
                Blocks = volume.BlockTables,
                Padded = Padded,
                FaceMasks = FaceMasks,
                Vertices = Vertices,
                Indices = Indices,
                Indices16 = Indices16,
                CollisionVertices = CollisionVertices,
                CollisionIndices = CollisionIndices,
                Bounds = Bounds,
            };
        }

        /// <summary>Waits for the job (if any) and releases the pinned arrays. Main thread only.</summary>
        public void Complete()
        {
            if (Scheduled)
            {
                Handle.Complete();
                Scheduled = false;
            }
            Unpin();
        }

        public void Dispose()
        {
            Complete();
            if (Padded.IsCreated) Padded.Dispose();
            if (FaceMasks.IsCreated) FaceMasks.Dispose();
            if (Neighbours.IsCreated) Neighbours.Dispose();
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Indices.IsCreated) Indices.Dispose();
            if (Indices16.IsCreated) Indices16.Dispose();
            if (CollisionVertices.IsCreated) CollisionVertices.Dispose();
            if (CollisionIndices.IsCreated) CollisionIndices.Dispose();
            if (Bounds.IsCreated) Bounds.Dispose();
        }
    }

    /// <summary>
    /// Cooks a collision mesh for a MeshCollider on a worker thread, so assigning it later costs next to nothing.
    /// </summary>
    public struct BakeColliderJob : IJob
    {
        public int MeshId;
        public bool Convex;
        public MeshColliderCookingOptions Options;

        public void Execute()
        {
#if UNITY_6000_0_OR_NEWER
            // Unity 6.3 marks the instance-ID overloads obsolete in favour of EntityId ones; these still work, and
            // keep the package building on 2022.3 and earlier Unity 6 versions
#pragma warning disable CS0618
            Physics.BakeMesh(MeshId, Convex, Options);
#pragma warning restore CS0618
#else
            Physics.BakeMesh(MeshId, Convex);
#endif
        }
    }
}
