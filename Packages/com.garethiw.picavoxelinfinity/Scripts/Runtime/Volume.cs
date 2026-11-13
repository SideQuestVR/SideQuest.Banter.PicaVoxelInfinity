/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
// 
/////////////////////////////////////////////////////////////////////////
using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxel
{
    /// <summary>
    /// Which type of mesh collider to use when generating meshes
    /// </summary>
    public enum CollisionMode
    {
        None,
        MeshColliderConvex,
        MeshColliderConcave
    }

    public enum MeshingMode
    {
        Greedy,
        Culled,
        Marching
    }

    [Serializable]
    public class CustomBlock
    {
        public int VoxelValue;
        public bool AllowOrientation;
        public bool HasTransparency;
        public bool UseTargetOrientationWhenSameValue;
        public bool UseBoxColliderForCursor;
        public Mesh Mesh;
    }

    public class CustomBlockData
    {
        public bool HasMesh;
        public Vector3[] Vertices;
        public Vector4[] UVs;
        public int[] Indices;
        public bool HasTransparency;
        public bool AllowOrientation;
        public bool UseTargetOrientationWhenSameValue;
        public bool UseBoxColliderForCursor;
    }

    /// <summary>
    /// The main parent script for a PicaVoxel volume
    /// </summary>
    [AddComponentMenu("PicaVoxel/PicaVoxel Volume")]
    [Serializable]
    [SelectionBase]
    [ExecuteAlways]
    public class Volume : MonoBehaviour
    {
        public GameObject ChunkPrefab;

        public int ChunkSize = 16;

        public float VoxelSize = 1f;

        public Vector3 Pivot = Vector3.zero;

        public Dictionary<(int, int, int), Chunk> Chunks = new();
        // Chunk data jobs (e.g. chunk processors, persisters) look up chunks from worker threads, so any
        // structural change to Chunks must be done under this lock.
        private readonly object _chunksLock = new object();

        public int MaxValue;
        public CustomBlock[] CustomBlocks = Array.Empty<CustomBlock>();
        [DoNotSerialize] public Dictionary<int, CustomBlockData> CustomBlocksDict = new();
        public Color[] ColorPalette;

        public VoxelManipulator.ManipulatorMode ManipulatorMode = VoxelManipulator.ManipulatorMode.Color;

        // Infinite mode will be for infinite terrain, which will keep generating as you move
        // Non-infinite will only add chunks when edited, and all chunks will be visible/rendered at startup
        public bool IsInfinite = false;
        public int InfiniteChunkRadius;
        public Vector3Int InfiniteChunkBounds = Vector3Int.zero;
        public float InfiniteUpdateInterval = 0.25f;
        public int InfiniteUpdateSlicing = 8;
        public int InfiniteChunkRadiusUpdateMargin = 3;

        public int GenerationSeed;

        public bool IsDataReady => _voxelDataGenerator.IsReady;
        public bool IsPersisterReady => _voxelDataPersister.IsReady;

        public string Identifier;

        public float PersistenceInterval = 2f;

        public bool DelayGeneration;
        
        // Chunk generation settings
        public MeshingMode MeshingMode;
        public MeshingMode MeshColliderMeshingMode;
        public bool GenerateMeshColliderSeparately = false;
        public Material Material;

#if UNITY_6000_0_OR_NEWER
        public PhysicsMaterial PhysicMaterial;
#else
        public PhysicMaterial PhysicMaterial;
#endif

        public bool CollisionTrigger;
        public CollisionMode CollisionMode;
        public float SelfShadingIntensity = 0.2f;
        public ShadowCastingMode CastShadows = ShadowCastingMode.On;
        public bool ReceiveShadows = true;
        public int ChunkLayer;
        [HideInInspector] public I_ChunkProcessor[] ChunkProcessors;

        // Performance (Culled meshing) //////////////////////////////////
        [Header("Performance")]
        [Tooltip("Chunks meshing at once, on Unity's job workers. 0 picks from the worker count.")]
        public int MaxConcurrentMeshJobs = 0;
        [Tooltip("Chunks generating voxel data at once, on the thread pool. 0 picks from the core count.")]
        public int MaxConcurrentDataJobs = 0;
        [Tooltip("Main-thread time per frame for uploading finished meshes, in ms (at least one a frame).")]
        public float MeshApplyBudgetMs = 2f;
        [Tooltip("Main-thread time per frame for creating or recycling chunks, in ms (at least one a frame).")]
        public float ChunkCreateBudgetMs = 2f;
        [Tooltip("Frames a chunk may wait for its neighbours to finish generating before meshing anyway.")]
        public int MaxNeighbourWaitFrames = 60;
        [Tooltip("Seconds a chunk that is already showing a mesh waits before re-meshing for a neighbour's new data, " +
                 "so several neighbours arriving together cost one re-mesh. Edits re-mesh at once.")]
        public float NeighbourRemeshDelay = 0.3f;
        [Tooltip("Merge coplanar faces in collision meshes: the same surfaces with far fewer triangles to cook.")]
        public bool MergeColliderFaces = true;
        [Tooltip("Re-mesh the outer ring of chunks every pass, as older versions did. Neighbours now re-mesh " +
                 "when they need to, so this only costs time.")]
        public bool PeriodicRemesh = false;

        private static int s_mainThreadId = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThread() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>Whether this is Unity's main thread (true before it is known).</summary>
        public static bool IsMainThread => s_mainThreadId < 0 || Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        // Culled chunks mesh in jobs scheduled here; other modes mesh on the thread pool as before
        internal bool UseMeshJobs;
        // Time.unscaledTime for this frame's update, read per chunk
        internal float FrameTime;

        // The custom blocks as native tables for mesh jobs, rebuilt when CustomBlocksDict changes size
        internal CustomBlockTables BlockTables;
        private int _blockTablesCount = -1;

        private readonly List<MeshJobContext> _meshContexts = new();
        private readonly Stack<MeshJobContext> _freeMeshContexts = new();
        private MeshJobContext _immediateMeshContext;
        private int _busyMeshContexts;
        private int _dataJobsInFlight;

        private readonly List<(int dist, Chunk chunk)> _dataCandidates = new();
        private readonly List<(int dist, Chunk chunk)> _meshCandidates = new();
        private readonly List<(int dist, Chunk chunk)> _finishedMeshes = new();
        private static readonly Comparison<(int dist, Chunk chunk)> NearestFirst = (a, b) => a.dist.CompareTo(b.dist);
        private readonly System.Diagnostics.Stopwatch _budgetWatch = new();
        private Vector3Int _focusChunk;

        // Positions waiting for a chunk, nearest last (they are taken from the end)
        private readonly List<(int x, int y, int z)> _createQueue = new();
        private readonly HashSet<(int x, int y, int z)> _createPending = new();
        private bool _createQueueDirty;
        private Vector3Int _createQueueSortedFor;
        private readonly HashSet<Chunk> _freeSet = new();
        private Comparison<(int x, int y, int z)> _farthestFirst;
        /////////////////////////////////////////////////////////////////

        private Transform _cameraTransform;
        private float _infiniteUpdateTimer;
        private I_VoxelDataGenerator _voxelDataGenerator;
        private I_VoxelDataPersister _voxelDataPersister;
        private bool _isFirstPass;
        private Vector3 _lastCamOffset;

        private void OnEnable()
        {
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (string.IsNullOrEmpty(Identifier))
            {
                Identifier = Guid.NewGuid().ToString();
            }
        }

        private void Start()
        {
            if (!Application.isPlaying)
                return;

            _voxelDataGenerator = GetComponent<I_VoxelDataGenerator>();
            if (_voxelDataGenerator == null)
            {
                _voxelDataGenerator = gameObject.AddComponent<InfiniteSolidGenerator>();
            }

            _voxelDataGenerator.Seed = GenerationSeed;

            _voxelDataPersister = GetComponent<I_VoxelDataPersister>();
            if (_voxelDataPersister == null)
            {
                _voxelDataPersister = gameObject.AddComponent<DiskPersister>();
            }

            ChunkProcessors = GetComponentsInChildren<I_ChunkProcessor>();
            ChunkProcessors = ChunkProcessors.OrderBy(cp => cp.Order).ToArray();

            _cameraTransform = Camera.main.transform;
            _lastCamOffset = _cameraTransform.position -
                             (transform.position - (ChunkSize * VoxelSize * 0.5f * Vector3.one));

            _isFirstPass = true;
            UseMeshJobs = MeshingMode == MeshingMode.Culled;

            CustomBlocksDict.Clear();
            foreach (CustomBlock cb in CustomBlocks)
            {
                CustomBlockData data = new CustomBlockData();
                if (cb.Mesh != null)
                {
                    data.Vertices = new Vector3[cb.Mesh.vertexCount];
                    data.UVs = new Vector4[cb.Mesh.vertexCount];
                    data.Indices = new int[cb.Mesh.triangles.Length];
                    for (var i = 0; i < cb.Mesh.vertices.Length; i++)
                        data.Vertices[i] = (cb.Mesh.vertices[i] * VoxelSize);
                    for (var i = 0; i < cb.Mesh.uv.Length; i++)
                        data.UVs[i] = new Vector4(cb.Mesh.uv[i].x, cb.Mesh.uv[i].y, cb.VoxelValue, 0);
                    for (var i = 0; i < cb.Mesh.triangles.Length; i++)
                        data.Indices[i] = cb.Mesh.triangles[i];
                    data.HasMesh = true;
                    data.AllowOrientation = cb.AllowOrientation;
                    data.UseTargetOrientationWhenSameValue = cb.UseTargetOrientationWhenSameValue;
                    data.UseBoxColliderForCursor = cb.UseBoxColliderForCursor;
                }

                data.HasTransparency = cb.HasTransparency;
                CustomBlocksDict.Add(cb.VoxelValue, data);
            }
        }

        Queue<Chunk> _freeChunks = new();
        private int slice = 0;
        private bool dataPass = true;
        Vector3Int thisUpdateccpos = Vector3Int.zero;
        int thisUpdateMargin = 1;

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (DelayGeneration)
                return;

            UpdateFocus();

            _dataCandidates.Clear();
            _meshCandidates.Clear();
            _finishedMeshes.Clear();
            UseMeshJobs = MeshingMode == MeshingMode.Culled;
            FrameTime = Time.unscaledTime;
            foreach (Chunk chunk in Chunks.Values)
            {
                chunk.CheckGeneration();
                chunk.Persist();

                if (!UseMeshJobs)
                    continue;
                // Most chunks want nothing most frames, so the distance is only worked out for those that do
                if (chunk.WantsData)
                    _dataCandidates.Add((FocusDistance(chunk.Position), chunk));
                if (chunk.HasMeshJob)
                {
                    if (chunk.HasFinishedMeshJob)
                        _finishedMeshes.Add((FocusDistance(chunk.Position), chunk));
                }
                else if (chunk.WantsMeshJob)
                    _meshCandidates.Add((FocusDistance(chunk.Position), chunk));
            }

            StartDataJobs();
            FinishMeshJobs();
            StartMeshJobs();

            if (!IsInfinite && _isFirstPass && IsDataReady)
            {
                GenerateFiniteChunks();
                _isFirstPass = false;
                thisUpdateMargin = InfiniteChunkRadiusUpdateMargin;
            }

            if (!IsInfinite)
                return;

            ProcessCreateQueue();

            _infiniteUpdateTimer += Time.deltaTime;
            if (_infiniteUpdateTimer >= InfiniteUpdateInterval)
            {
                _infiniteUpdateTimer = 0;

                float radunit = ChunkSize * VoxelSize;
                Vector3 halfchunk = radunit * 0.5f * Vector3.one;
                Vector3 camoffset = _cameraTransform.position - (transform.position - halfchunk);
                Vector3 camChunkPos = camoffset / radunit;
                Vector3Int ccpos = new Vector3Int(Mathf.RoundToInt(camChunkPos.x), Mathf.RoundToInt(camChunkPos.y),
                    Mathf.RoundToInt(camChunkPos.z));


                //Debug.Log(ccpos);
                //float rad2 = ((InfiniteChunkRadius*radunit)*(InfiniteChunkRadius*radunit)) + ((radunit * 0.5f) * (radunit * 0.5f));

                // If the camera moves more than a chunk in one go, regenerate the whole lot starting with the chunk at camera position
                if ((camoffset - _lastCamOffset).magnitude > radunit)
                {
                    // foreach (Chunk chunk in Chunks.Values)
                    // {
                    //     if (!_freeChunks.Contains(chunk))
                    //         _freeChunks.Enqueue(chunk);
                    // }
                    // foreach (Chunk chunk in _freeChunks)
                    //     Chunks.Remove(chunk.Position);
                    //_isFirstPass = true;
                    dataPass = true;
                    slice = 0;
                    thisUpdateccpos = ccpos;
                    thisUpdateMargin = InfiniteChunkRadius;
                    // if (_freeChunks.TryDequeue(out Chunk reuse))
                    // {
                    //     Chunks.Add((ccpos.x,ccpos.y,ccpos.z), reuse);
                    //     reuse.Initialize((ccpos.x,ccpos.y,ccpos.z), this);
                    // }
                }

                _lastCamOffset = camoffset;

                if (slice == 0)
                {
                    thisUpdateccpos = ccpos;

                    foreach (Chunk chunk in Chunks.Values)
                    {
                        Vector3Int cpos = new Vector3Int(chunk.Position.x, chunk.Position.y, chunk.Position.z);

                        if ((ccpos - cpos).sqrMagnitude > InfiniteChunkRadius * InfiniteChunkRadius)
                        {
                            if (_freeSet.Add(chunk))
                                _freeChunks.Enqueue(chunk);
                        }
                    }

                    lock (_chunksLock)
                    {
                        foreach (Chunk chunk in _freeChunks)
                        {
                            if (Chunks.TryGetValue(chunk.Position, out Chunk existing) && existing == chunk)
                            {
                                Chunks.Remove(chunk.Position);
                                // Hide it and drop its collider; otherwise it stays visible/solid at its old
                                // position until it happens to be re-used
                                chunk.Release();
                            }
                        }
                    }

                    PrunePendingWrites();

                    slice++;
                    return;
                }

                int s = 1;
                // The loops stop at the volume's bounds rather than skipping every position past them (the same
                // positions, in the same order, so slicing is unchanged)
                SweepRange(thisUpdateccpos.x, InfiniteChunkBounds.x, out int x0, out int x1);
                SweepRange(thisUpdateccpos.y, InfiniteChunkBounds.y, out int y0, out int y1);
                SweepRange(thisUpdateccpos.z, InfiniteChunkBounds.z, out int z0, out int z1);
                for (int z = z0; z < z1; z++)
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            if ((InfiniteChunkBounds.x > 0 &&
                                 (x < -InfiniteChunkBounds.x || x > InfiniteChunkBounds.x)) ||
                                (InfiniteChunkBounds.y > 0 &&
                                 (y < -InfiniteChunkBounds.y || y > InfiniteChunkBounds.y)) ||
                                (InfiniteChunkBounds.z > 0 &&
                                 (z < -InfiniteChunkBounds.z || z > InfiniteChunkBounds.z)))
                                continue;

                            Vector3 cpos = new Vector3(x, y, z);
                            if ((thisUpdateccpos - cpos).sqrMagnitude > InfiniteChunkRadius * InfiniteChunkRadius)
                                continue;

                            if (!_isFirstPass)
                            {
                                s++;
                                if (s == InfiniteUpdateSlicing)
                                    s = 1;
                                if (s != slice)
                                    continue;
                                // Only the outer ring is checked, the inside being there already. After a jump the
                                // margin is the whole radius and nothing is skipped (<= 0 skipped the camera's own
                                // chunk, which was then never created: a hole where a far respawn landed)
                                if (thisUpdateMargin < InfiniteChunkRadius &&
                                    (thisUpdateccpos - cpos).sqrMagnitude <= (InfiniteChunkRadius - thisUpdateMargin) *
                                    (InfiniteChunkRadius - thisUpdateMargin))
                                    continue;
                            }

                            if (Chunks.TryGetValue((x, y, z), out Chunk existingChunk))
                            {
                                if (!dataPass && PeriodicRemesh)
                                {
                                    existingChunk.SetMeshDirty();
                                }

                                continue;
                            }

                            // Created (or recycled) by ProcessCreateQueue, nearest first and a few a frame
                            QueueCreate((x, y, z));
                        }

                if (_isFirstPass)
                {
                    if (!dataPass)
                        _isFirstPass = false;
                }

                slice++;
                if (slice == InfiniteUpdateSlicing)
                {
                    if (!dataPass)
                        thisUpdateMargin = InfiniteChunkRadiusUpdateMargin;

                    slice = dataPass ? 1 : 0;

                    dataPass = !dataPass;
                }
            }
        }

        // Scheduling //////////////////////////////////////////////////////

        // The infinite sweep's range on one axis: radius around the centre, both ends included (hi is exclusive),
        // cut to the bounds when there are some
        private void SweepRange(int centre, int bound, out int lo, out int hi)
        {
            lo = centre - InfiniteChunkRadius;
            hi = centre + InfiniteChunkRadius + 1;
            if (bound > 0)
            {
                lo = Math.Max(lo, -bound);
                hi = Math.Min(hi, bound + 1);
            }
        }

        // The chunk the camera is in: work is done nearest to it first
        private void UpdateFocus()
        {
            if (_cameraTransform == null)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                _cameraTransform = cam.transform;
            }
            float radunit = ChunkSize * VoxelSize;
            Vector3 camoffset = _cameraTransform.position - (transform.position - radunit * 0.5f * Vector3.one);
            Vector3 c = camoffset / radunit;
            _focusChunk = new Vector3Int(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y), Mathf.RoundToInt(c.z));
        }

        private int FocusDistance((int x, int y, int z) p)
        {
            int dx = p.x - _focusChunk.x, dy = p.y - _focusChunk.y, dz = p.z - _focusChunk.z;
            return dx * dx + dy * dy + dz * dz;
        }

        // Data jobs run on thread-pool threads, which compete with the main and render threads and Unity's job
        // workers; on a phone-class CPU (Quest) half the cores is plenty
        private int DataJobLimit => MaxConcurrentDataJobs > 0
            ? MaxConcurrentDataJobs
            : Mathf.Clamp(SystemInfo.processorCount / 2, 1, 4);

        private int MeshJobLimit => MaxConcurrentMeshJobs > 0
            ? MaxConcurrentMeshJobs
            : Mathf.Clamp(JobsUtility.JobWorkerCount, 2, 8);

        internal void BeginDataJob() => Interlocked.Increment(ref _dataJobsInFlight);
        internal void EndDataJob() => Interlocked.Decrement(ref _dataJobsInFlight);

        private void StartDataJobs()
        {
            int free = DataJobLimit - Volatile.Read(ref _dataJobsInFlight);
            if (free <= 0 || _dataCandidates.Count == 0)
                return;
            if (_dataCandidates.Count > free)
                _dataCandidates.Sort(NearestFirst);
            for (int i = 0; i < _dataCandidates.Count && i < free; i++)
                _dataCandidates[i].chunk.StartDataJob();
        }

        // Uploads finished meshes, nearest first, until this frame's budget is spent (always at least one)
        private void FinishMeshJobs()
        {
            if (_finishedMeshes.Count == 0)
                return;
            _finishedMeshes.Sort(NearestFirst);
            _budgetWatch.Restart();
            for (int i = 0; i < _finishedMeshes.Count; i++)
            {
                if (i > 0 && _budgetWatch.Elapsed.TotalMilliseconds >= MeshApplyBudgetMs)
                    break;
                _finishedMeshes[i].chunk.FinishMeshJob();
            }
        }

        private void StartMeshJobs()
        {
            if (_meshCandidates.Count == 0)
                return;
            int free = MeshJobLimit - _busyMeshContexts;
            if (free <= 0)
                return;

            EnsureBlockTables();
            _meshCandidates.Sort(NearestFirst);
            int started = 0;
            // Waiting for neighbours costs 27 lookups a check, so only so many are checked a frame
            int checks = 32;
            for (int i = 0; i < _meshCandidates.Count && started < free; i++)
            {
                Chunk chunk = _meshCandidates[i].chunk;
                if (chunk.NeighbourWaitFrames < MaxNeighbourWaitFrames)
                {
                    if (checks-- <= 0)
                        break;
                    // Mesh once the neighbours are in, rather than now and again as each of them finishes
                    if (!chunk.NeighboursSettled())
                    {
                        chunk.NeighbourWaitFrames++;
                        continue;
                    }
                }
                chunk.ScheduleMeshJob(RentMeshContext());
                started++;
            }
            if (started > 0)
                JobHandle.ScheduleBatchedJobs();
        }

        private MeshJobContext RentMeshContext()
        {
            _busyMeshContexts++;
            if (_freeMeshContexts.Count > 0)
                return _freeMeshContexts.Pop();
            var ctx = new MeshJobContext(ChunkSize);
            _meshContexts.Add(ctx);
            return ctx;
        }

        internal void ReturnMeshContext(MeshJobContext ctx)
        {
            if (ctx == _immediateMeshContext)
                return;
            _busyMeshContexts--;
            _freeMeshContexts.Push(ctx);
        }

        /// <summary>The context edits mesh in, on the main thread.</summary>
        internal MeshJobContext ImmediateMeshContext
        {
            get
            {
                EnsureBlockTables();
                if (_immediateMeshContext == null)
                {
                    _immediateMeshContext = new MeshJobContext(ChunkSize);
                    _meshContexts.Add(_immediateMeshContext);
                }
                return _immediateMeshContext;
            }
        }

        private void CompleteMeshJobs()
        {
            foreach (MeshJobContext ctx in _meshContexts)
                ctx.Handle.Complete();
        }

        private void EnsureBlockTables()
        {
            if (BlockTables.IsCreated && _blockTablesCount == CustomBlocksDict.Count)
                return;
            // Jobs in flight read the old tables
            CompleteMeshJobs();
            BlockTables.Dispose();
            BlockTables = BuildBlockTables(CustomBlocksDict);
            _blockTablesCount = CustomBlocksDict.Count;
        }

        /// <summary>Flattens the custom blocks into the tables mesh jobs read. Main thread only.</summary>
        public static CustomBlockTables BuildBlockTables(Dictionary<int, CustomBlockData> blocks)
        {
            var flags = new NativeArray<byte>(256, Allocator.Persistent);
            var ranges = new NativeArray<int4>(256, Allocator.Persistent);
            var verts = new List<float3>();
            var normals = new List<float3>();
            var uvs = new List<float2>();
            var indices = new List<int>();
            Mesh scratch = null;

            foreach (KeyValuePair<int, CustomBlockData> kv in blocks)
            {
                if (kv.Key < 0 || kv.Key > 255 || kv.Value == null)
                    continue;
                CustomBlockData cb = kv.Value;
                byte f = CustomBlockTables.IsCustom;
                if (cb.HasTransparency) f |= CustomBlockTables.Transparent;
                if (cb.AllowOrientation) f |= CustomBlockTables.AllowOrientation;

                if (cb.HasMesh && cb.Vertices != null && cb.Indices != null)
                {
                    f |= CustomBlockTables.HasMesh;
                    // The normals RecalculateNormals gave these vertices in the old managed mesher
                    if (scratch == null) scratch = new Mesh();
                    scratch.Clear();
                    scratch.indexFormat = cb.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    scratch.SetVertices(cb.Vertices);
                    scratch.SetTriangles(cb.Indices, 0);
                    scratch.RecalculateNormals();
                    Vector3[] n = scratch.normals;

                    ranges[kv.Key] = new int4(verts.Count, cb.Vertices.Length, indices.Count, cb.Indices.Length);
                    for (int i = 0; i < cb.Vertices.Length; i++)
                    {
                        verts.Add(cb.Vertices[i]);
                        normals.Add(i < n.Length ? (float3)n[i] : float3.zero);
                        Vector4 uv = cb.UVs != null && i < cb.UVs.Length ? cb.UVs[i] : Vector4.zero;
                        uvs.Add(new float2(uv.x, uv.y));
                    }
                    indices.AddRange(cb.Indices);
                }
                flags[kv.Key] = f;
            }

            if (scratch != null)
            {
                if (Application.isPlaying) Destroy(scratch);
                else DestroyImmediate(scratch);
            }

            return new CustomBlockTables
            {
                Flags = flags,
                MeshRanges = ranges,
                Vertices = ToNative(verts),
                Normals = ToNative(normals),
                UVs = ToNative(uvs),
                Indices = ToNative(indices),
            };
        }

        private static NativeArray<T> ToNative<T>(List<T> list) where T : struct
        {
            // Never empty, so the job always has a valid array to read
            var arr = new NativeArray<T>(Math.Max(1, list.Count), Allocator.Persistent);
            for (int i = 0; i < list.Count; i++)
                arr[i] = list[i];
            return arr;
        }

        // Writes from chunk processors into positions that had no chunk yet, applied when one is filled there
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int y, int z),
            System.Collections.Concurrent.ConcurrentQueue<(int index, byte state, byte value)>> _pendingWrites = new();

        /// <summary>
        /// Sets a voxel's state and value in the chunk at <paramref name="chunkPos"/> on behalf of a chunk processor
        /// working on a neighbour (trees reaching over a border). Safe from any thread. A write for a chunk not filled
        /// yet, or not created yet (infinite volumes), is kept until it is, instead of being lost to the generator.
        /// </summary>
        public void WriteVoxelFromNeighbour((int x, int y, int z) chunkPos, int index, byte state, byte value)
        {
            Chunk c = GetChunk(chunkPos);
            if (!ReferenceEquals(c, null))
            {
                c.WriteVoxelFromNeighbour(index, state, value);
                return;
            }
            if (!IsInfinite)
                return;
            _pendingWrites.GetOrAdd(chunkPos, _ => new System.Collections.Concurrent.ConcurrentQueue<(int, byte, byte)>())
                .Enqueue((index, state, value));
        }

        // Called by a chunk as soon as its voxels are filled
        internal void ApplyPendingWrites((int x, int y, int z) chunkPos, Voxel[] voxels, ref bool hasData)
        {
            if (_pendingWrites.IsEmpty || !_pendingWrites.TryRemove(chunkPos, out var writes))
                return;
            while (writes.TryDequeue(out var w))
            {
                voxels[w.index].State = w.state;
                voxels[w.index].Value = w.value;
                if (w.state != 0) hasData = true;
            }
        }

        // Drops held writes for positions well out of range: by the time one comes back, the chunk that wrote them
        // will have been regenerated and will write them again
        private void PrunePendingWrites()
        {
            if (_pendingWrites.IsEmpty)
                return;
            int limit = (InfiniteChunkRadius + 2) * (InfiniteChunkRadius + 2);
            foreach (var key in _pendingWrites.Keys)
                if (FocusDistance(key) > limit)
                    _pendingWrites.TryRemove(key, out _);
        }

        /// <summary>Whether a chunk is queued to be created at this position.</summary>
        internal bool IsCreatePending((int x, int y, int z) pos) => _createPending.Contains(pos);

        private void QueueCreate((int x, int y, int z) pos)
        {
            if (_createPending.Add(pos))
            {
                _createQueue.Add(pos);
                _createQueueDirty = true;
            }
        }

        private bool InCreateRange((int x, int y, int z) p)
        {
            if ((InfiniteChunkBounds.x > 0 && (p.x < -InfiniteChunkBounds.x || p.x > InfiniteChunkBounds.x)) ||
                (InfiniteChunkBounds.y > 0 && (p.y < -InfiniteChunkBounds.y || p.y > InfiniteChunkBounds.y)) ||
                (InfiniteChunkBounds.z > 0 && (p.z < -InfiniteChunkBounds.z || p.z > InfiniteChunkBounds.z)))
                return false;
            return FocusDistance(p) <= InfiniteChunkRadius * InfiniteChunkRadius;
        }

        // Creates (or recycles) queued chunks, nearest first, until this frame's budget is spent (at least one).
        // Creating every chunk in range in one frame cost hundreds of ms on the first pass.
        private void ProcessCreateQueue()
        {
            if (_createQueue.Count == 0)
                return;
            if (_createQueueDirty || _createQueueSortedFor != _focusChunk)
            {
                // Farthest first, so the nearest is at the end
                _farthestFirst ??= (a, b) => FocusDistance(b).CompareTo(FocusDistance(a));
                _createQueue.Sort(_farthestFirst);
                _createQueueDirty = false;
                _createQueueSortedFor = _focusChunk;
            }

            _budgetWatch.Restart();
            int created = 0;
            while (_createQueue.Count > 0)
            {
                if (created > 0 && _budgetWatch.Elapsed.TotalMilliseconds >= ChunkCreateBudgetMs)
                    break;
                (int x, int y, int z) pos = _createQueue[_createQueue.Count - 1];
                _createQueue.RemoveAt(_createQueue.Count - 1);
                _createPending.Remove(pos);
                if (Chunks.ContainsKey(pos) || !InCreateRange(pos))
                    continue;

                if (_freeChunks.TryDequeue(out Chunk reuse))
                {
                    _freeSet.Remove(reuse);
                    reuse.Initialize(pos, this);
                    lock (_chunksLock)
                        Chunks.Add(pos, reuse);
                }
                else
                {
                    Chunk newChunk = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                    newChunk.Initialize(pos, this);
                    lock (_chunksLock)
                        Chunks[pos] = newChunk;
                }
                created++;
                PicaVoxelStats.ChunksPlaced++;
            }
        }

        private void OnDestroy()
        {
            foreach (MeshJobContext ctx in _meshContexts)
                ctx.Dispose();
            _meshContexts.Clear();
            _freeMeshContexts.Clear();
            _immediateMeshContext = null;
            _busyMeshContexts = 0;
            BlockTables.Dispose();
            BlockTables = default;
            _blockTablesCount = -1;
        }

        /////////////////////////////////////////////////////////////////

        private void GenerateFiniteChunks()
        {
            if (_voxelDataGenerator is MagicaVoxelGenerator mvg)
            {
                Vector3Int size = mvg.ModelSize;
                int maxX = (size.x + ChunkSize - 1) / ChunkSize;
                int maxY = (size.y + ChunkSize - 1) / ChunkSize;
                int maxZ = (size.z + ChunkSize - 1) / ChunkSize;

                Voxel v = new Voxel();
                for (int cx = 0; cx < maxX; cx++)
                    for (int cy = 0; cy < maxY; cy++)
                        for (int cz = 0; cz < maxZ; cz++)
                        {
                            bool hasVoxel = false;
                            for (int x = cx * ChunkSize; x < (cx + 1) * ChunkSize; x++)
                            {
                                for (int y = cy * ChunkSize; y < (cy + 1) * ChunkSize; y++)
                                {
                                    for (int z = cz * ChunkSize; z < (cz + 1) * ChunkSize; z++)
                                    {
                                        if (mvg.GenerateVoxel(x, y, z, ref v))
                                        {
                                            hasVoxel = true;
                                            break;
                                        }
                                    }
                                    if (hasVoxel) break;
                                }
                                if (hasVoxel) break;
                            }

                            if (hasVoxel)
                            {
                                if (!Chunks.ContainsKey((cx, cy, cz)))
                                {
                                    Chunks[(cx, cy, cz)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                    Chunks[(cx, cy, cz)].Initialize((cx, cy, cz), this);
                                }
                            }
                        }
                return;
            }

            bool foundvox = true;

            int r = 1;

            Voxel v2 = new Voxel();
            if (_voxelDataGenerator.GenerateVoxel(0, 0, 0, ref v2))
            {
                if (!Chunks.ContainsKey((0, 0, 0)))
                    Chunks[(0, 0, 0)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                Chunks[(0, 0, 0)].Initialize((0, 0, 0), this);
            }

            while (foundvox)
            {
                foundvox = false;
                for (int x = -r; x <= r; x++)
                    for (int y = -r; y <= r; y++)
                        for (int z = -r; z <= r; z++)
                        {
                            if ((x != -r && x != r) && (y != -r && y != r) && (z != -r && z != r))
                                continue;

                            if (_voxelDataGenerator.GenerateVoxel(x, y, z, ref v2))
                            {
                                foundvox = true;

                                if (!Chunks.ContainsKey((x / ChunkSize, y / ChunkSize, z / ChunkSize)))
                                {
                                    Chunks[(x / ChunkSize, y / ChunkSize, z / ChunkSize)] =
                                        Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                    Chunks[(x / ChunkSize, y / ChunkSize, z / ChunkSize)]
                                        .Initialize((x / ChunkSize, y / ChunkSize, z / ChunkSize), this);
                                }
                            }
                        }

                r += ChunkSize;
            }
        }

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            _voxelDataGenerator.GenerateVoxel(x, y, z, ref voxel);
        }

        [CanBeNull]
        public Chunk GetChunk((int x, int y, int z) pos)
        {
            lock (_chunksLock)
                return Chunks.GetValueOrDefault(pos);
        }

        public Voxel? GetVoxelAtWorldPosition(Vector3 worldPos, out Chunk chunk, out (int x, int y, int z) pos)
        {
            pos = (0, 0, 0);
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            localPos -= Vector3.one * ChunkSize * 0.5f * VoxelSize;
            chunk = GetChunk(((int)(Mathf.Ceil(localPos.x / (ChunkSize * VoxelSize))),
                (int)(Mathf.Ceil(localPos.y / (ChunkSize * VoxelSize))),
                (int)Mathf.Ceil((localPos.z / (ChunkSize * VoxelSize)))));
            if (!chunk)
                return null;

            localPos -= chunk.transform.localPosition - (Vector3.one * ChunkSize * 0.5f * VoxelSize);

            pos = ((int)(localPos.x / (VoxelSize)), (int)(localPos.y / (VoxelSize)), (int)(localPos.z / (VoxelSize)));
            return chunk.GetVoxel(pos);
        }

        public Voxel? SetVoxelAtWorldPosition(Vector3 worldPos, Voxel newValue, out Chunk chunk,
            out (int x, int y, int z) pos, bool persist = true)
        {
            pos = (0, 0, 0);
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            localPos -= Vector3.one * ChunkSize * 0.5f * VoxelSize;
            chunk = GetChunk(((int)(Mathf.Ceil(localPos.x / (ChunkSize * VoxelSize))),
                (int)(Mathf.Ceil(localPos.y / (ChunkSize * VoxelSize))),
                (int)Mathf.Ceil((localPos.z / (ChunkSize * VoxelSize)))));
            if (!chunk)
                return null;

            localPos -= chunk.transform.localPosition - (Vector3.one * ChunkSize * 0.5f * VoxelSize);

            pos = ((int)(localPos.x / (VoxelSize)), (int)(localPos.y / (VoxelSize)), (int)(localPos.z / (VoxelSize)));
            Voxel? v = chunk.SetVoxel(pos, newValue, persist);

            return v;
        }

        public void SetVoxelByEditEvent(VoxelEditEventArgs e, bool persist = false)
        {
            if (e.VolumeId != Identifier)
                return;
            GetChunk((e.ChunkX, e.ChunkY, e.ChunkZ))?.SetVoxelByEditEvent(e, persist);
        }

        public bool LoadChunkChanges((int x, int y, int z) pos)
        {
            if (!_voxelDataPersister.IsReady)
                return false;

            return _voxelDataPersister.LoadChunk(this, pos.x, pos.y, pos.z);
        }

        public bool SaveChunkChanges((int x, int y, int z) pos, byte[] data)
        {
            if (!_voxelDataPersister.IsReady)
                return false;

            return _voxelDataPersister.SaveChunk(this, pos.x, pos.y, pos.z, data);
        }

        public void SetChunkChangesByChangesEvent(ChunkChangesEventArgs e)
        {
            if (e.VolumeId != Identifier)
                return;

            GetChunk((e.ChunkX, e.ChunkY, e.ChunkZ))?.LoadChanges(e.Data);
        }

        public void RegenerateMeshes(bool immediate = false)
        {
            foreach (Chunk c in Chunks.Values)
            {
                if (c)
                {
                    c.GenerateMesh(immediate);
                }
            }
        }
    }
}