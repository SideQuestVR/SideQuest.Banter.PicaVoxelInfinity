/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System.Threading;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace PicaVoxel
{
    public class Chunk : MonoBehaviour
    {
        public Voxel[] Voxels;

        public Volume Volume
        {
            get => _volume;
        }
        private Volume _volume;

        // Position of chunk in volume
        public (int x, int y, int z) Position;

        // Calculation stuff ////////////////////////////////////////////
        private enum ChunkStatus
        {
            NoChange,
            CalculatingMesh,
            Ready
        }

        // Is chunk free to be reused?
        public bool CanBeReused;

        private volatile bool _isDataDirty = false;
        private bool _isChangesDirty = false;
        private volatile bool _isMeshDirty = false;
        private volatile ChunkStatus status = ChunkStatus.NoChange;

        // Incremented every time this chunk is (re)initialized or released. Background data/mesh jobs capture the
        // value they were started with and throw their results away if the chunk has since been recycled.
        private volatile int _generation = 0;
        private readonly object _generationLock = new object();
        // True while a background data generation job is running for this chunk (of any generation)
        private volatile bool _isGeneratingData = false;
        // True once voxel data for the current generation has been fully generated
        private volatile bool _dataReady = false;
        // Set when this chunk's voxels change, so face-adjacent neighbours re-mesh their culled border faces
        private volatile bool _notifyNeighbours = false;
        // Set (from any thread) when something other than this chunk wrote into its voxels, e.g. a neighbour's trees
        private volatile bool _externalChange = false;
        // Generation the in-flight / last mesh job was started for, and whether it failed
        private int _meshJobGeneration = -1;
        private volatile bool _meshJobFailed = false;

        private float _persistTime = 0f;

        private Dictionary<(int x, int y, int z), Voxel> _changes = new();

        // Persisted edits handed over from another thread, applied on the main thread (see LoadChanges)
        private readonly ConcurrentQueue<(int generation, byte[] data)> _pendingLoads = new();
        // Writes from other chunks' processors that arrived before this position was filled (see WriteVoxelFromNeighbour)
        private readonly ConcurrentQueue<(int generation, int index, byte state, byte value)> _deferredWrites = new();
        // This generation's voxels have been filled by the generator (guarded by _generationLock)
        private bool _filled;
        private int _pendingLoadCount;

        // Managed vertex lists, only for the Greedy and Marching meshers (Culled meshes in a job, see below)
        private List<Vector3> vertices;
        private List<Vector4> uvs;
        private List<Color32> colors;
        private List<int> indexes;

        private MeshFilter mf;
        private MeshCollider mc;
        private MeshRenderer mr;

        private bool hasCreatedRuntimeMesh = false;
        private bool hasCreatedRuntimeColMesh = false;
        private bool updateColliderNextFrame = false;
        private volatile bool _disableMrNextFrame= false;

        // Culled meshing in a job ////////////////////////////////////////
        // The context of the mesh job in flight (or finished and waiting to be applied), null when none
        private MeshJobContext _meshJob;
        // Frames a mesh has waited for neighbours that are still generating (see Volume)
        internal int NeighbourWaitFrames;
        // A re-mesh asked for by a neighbour waits until then (Volume.FrameTime), see SetMeshDirtySoon
        private float _remeshAfter;
        // Two collision meshes: one is on the collider while the other is filled and cooked on a worker
        private readonly Mesh[] _colMeshes = new Mesh[2];
        private int _colActive = -1;
        private int _bakeIndex = -1;
        private int _bakeGeneration;
        private JobHandle _bakeHandle;
        private bool _baking;
        // What the renderer and collider were last set to, so per-frame checks never call into the engine
        private bool _rendererOn;
        private bool _colliderOn;
        private bool _rendererConfigured;
        // Whether mf.sharedMesh was made here (and so is ours to destroy), rather than set on the prefab
        private bool _ownsRenderMesh;
        /////////////////////////////////////////////////////////////////

        private const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        public void Initialize((int, int, int) pos, Volume parentVolume)
        {
            _volume = parentVolume;
            if (mf is null) mf = GetComponent<MeshFilter>();
            if (mr is null) mr = GetComponent<MeshRenderer>();
            if (mc is null) mc = GetComponent<MeshCollider>();
            // Invalidate any background jobs still running for this chunk's previous position, and clear out
            // everything left over from it (mesh, collider, pending edits) before it is moved.
            lock (_generationLock)
            {
                _generation++;
                _filled = false;
            }
            ResetState();
            Position = pos;
            _volume.CellRegister(this);
            transform.localPosition = new Vector3((Position.x*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.y*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.z*Volume.ChunkSize)-(Volume.ChunkSize*0.5f)) * Volume.VoxelSize;
            gameObject.layer = Volume.ChunkLayer;
            if(Voxels==null || Voxels.Length==0)
                Voxels = new Voxel[Volume.ChunkSize * Volume.ChunkSize * Volume.ChunkSize];
            if (mf.sharedMesh is null)
            {
                mf.sharedMesh = new Mesh { name = "PicaVoxel Chunk" };
                _ownsRenderMesh = true;
            }
            _isDataDirty = true;
            #if UNITY_EDITOR
            gameObject.name = $"Chunk {Position.x},{Position.y},{Position.z}";
            #endif
        }

        private volatile bool _hasData = false;

        /// <summary>
        /// Resets all per-position state so a chunk can be safely recycled. Main thread only.
        /// </summary>
        private void ResetState()
        {
            CaveCulled = false;
            SetRenderer(false, true);
            if (mc is not null)
            {
                // Only switched off: the collision mesh stays on it (and _colActive with it), so the next one
                // assigned is the other of the pair. Nulling it and then assigning the same mesh again was
                // silently ignored by the MeshCollider, leaving a recycled chunk visible but with no collider.
                mc.enabled = false;
                _colliderOn = false;
            }
            if (mf is not null && mf.sharedMesh is not null)
                mf.sharedMesh.Clear();

            _hasData = false;
            _dataReady = false;
            _isDataDirty = false;
            _isMeshDirty = false;
            _notifyNeighbours = false;
            _externalChange = false;
            _disableMrNextFrame = false;
            updateColliderNextFrame = false;
            NeighbourWaitFrames = 0;
            _remeshAfter = 0f;
            _changes.Clear();
            while (_pendingLoads.TryDequeue(out _)) { }
            while (_deferredWrites.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _pendingLoadCount, 0);
            _persistTime = 0f;
        }

        /// <summary>
        /// Called by the volume when this chunk goes out of range and is put in the pool for re-use.
        /// Hides it, removes its collider and invalidates any background work still running for it.
        /// </summary>
        public void Release()
        {
            // Don't lose edits that haven't been persisted yet
            if (_changes.Count > 0 && _persistTime > 0f)
                SaveChanges();

            _volume.CellUnregister(this);
            lock (_generationLock)
            {
                _generation++;
                _filled = false;
            }
            ResetState();
        }

        private void GenerateData(int generation, (int x, int y, int z) pos)
        {
            Volume volume = _volume;
            try
            {
                if (!volume.IsDataReady)
                {
                    _isDataDirty = true;
                    return;
                }

                bool hasData = false;
                int cs = volume.ChunkSize;
                //Debug.Log($"Chunk {pos.x},{pos.y},{pos.z} is doing data gen");
                // Columns (x, z) outermost and y innermost: a height-map generator then sees the same column
                // ChunkSize times in a row, which lets it work out the column once (see TerrainGenerator).
                for (int z = 0; z < cs; z++)
                {
                    // Chunk was recycled while we were generating; abandon this job
                    if (generation != _generation)
                        return;

                    for (int x = 0; x < cs; x++)
                        for (int y = 0; y < cs; y++)
                        {
                            int i = x + cs * (y + cs * z);
                            volume.GenerateVoxel(x + (cs * pos.x), y + (cs * pos.y), z + (cs * pos.z), ref Voxels[i]);
                            if (!hasData && Voxels[i].Active)
                                hasData = true;
                        }
                }

                lock (_generationLock)
                {
                    if (generation != _generation)
                        return;

                    // Voxels neighbours' processors wrote for this position before it was filled (trees from next
                    // door), which filling would otherwise have overwritten
                    while (_deferredWrites.TryDequeue(out var w))
                    {
                        if (w.generation != generation) continue;
                        Voxels[w.index].State = w.state;
                        Voxels[w.index].Value = w.value;
                        if (w.state != 0) hasData = true;
                    }
                    volume.ApplyPendingWrites(pos, Voxels, ref hasData);

                    _filled = true;
                    _hasData = hasData;
                }

                foreach (I_ChunkProcessor p in volume.ChunkProcessors)
                {
                    if (p.Schedule == ProcessingSchedule.AfterGeneration)
                        p.ProcessChunk(volume, this);
                }
                //Debug.Log($"Chunk {pos.x},{pos.y},{pos.z} is finished data gen");

                // May hand back persisted edits (applied on the main thread, see LoadChanges)
                volume.LoadChunkChanges(pos);

                lock (_generationLock)
                {
                    if (generation != _generation)
                        return;

                    _dataReady = true;

                    // Always queue a mesh once data is ready (previously only in finite mode, which left infinite
                    // chunks without a mesh/collider if the volume's mesh pass reached them before data finished).
                    if (_hasData)
                        _isMeshDirty = true;
                    else
                        _disableMrNextFrame = true;

                    // Neighbours may have meshed against this chunk's stale/incomplete voxels, re-mesh their borders
                    _notifyNeighbours = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // Retry on the next update rather than leaving the chunk permanently empty
                if (generation == _generation)
                    _isDataDirty = true;
            }
            finally
            {
                _isGeneratingData = false;
                volume.EndDataJob();
            }
        }

        public Voxel? GetVoxel((int x, int y, int z) pos)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            return Voxels[i];
        }

        public Voxel? SetVoxel((int x, int y, int z) pos, Voxel newValue, bool persist = true)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            Voxels[i] = newValue;
            _changes[pos] = newValue;
            if (persist)
            {
                _persistTime = _volume.PersistenceInterval;
            }

            EvaluateData();
            GenerateMesh(true);

            // Re-mesh the neighbours whose meshes can see this voxel: those it borders on every axis they're offset
            // along (a face neighbour for an edge voxel, an edge or corner neighbour for an edge or corner voxel,
            // whose self-shading samples it). Previously any one border axis dirtied a whole 3x3 slab of chunks.
            int last = Volume.ChunkSize - 1;
            for (int dz = -1; dz <= 1; dz++)
            {
                if ((dz == -1 && pos.z != 0) || (dz == 1 && pos.z != last)) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    if ((dy == -1 && pos.y != 0) || (dy == 1 && pos.y != last)) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == -1 && pos.x != 0) || (dx == 1 && pos.x != last)) continue;
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        Volume.GetChunk((Position.x + dx, Position.y + dy, Position.z + dz))?.SetMeshDirty();
                    }
                }
            }

            return Voxels[i];
        }

        public void SetVoxelByEditEvent(VoxelEditEventArgs e, bool persist = false)
        {
            Voxel v = new Voxel()
            {
                State = e.VoxelState,
                Value = e.VoxelValue,
                Color = e.VoxelColor
            };

            SetVoxel((e.VoxelX,e.VoxelY,e.VoxelZ), v, persist);
        }

        public void Persist()
        {
            if (_changes.Count == 0)
                return;

            if (_persistTime <= 0f)
                return;

            _persistTime -= Time.deltaTime;
            if (_persistTime <= 0f)
            {
                if (SaveChanges())
                {
                    _persistTime = 0;
                }
            }
        }

        /// <summary>
        /// Sets a voxel's state and value on behalf of another chunk (its trees reaching over the border, say), keeping
        /// its color. Safe from any thread. If this chunk hasn't been filled yet, the write is kept and applied as soon
        /// as it is, rather than being overwritten by the generator; otherwise it's made now and the chunk re-meshes.
        /// </summary>
        public void WriteVoxelFromNeighbour(int index, byte state, byte value)
        {
            bool written;
            lock (_generationLock)
            {
                written = _filled;
                if (written)
                {
                    Voxels[index].State = state;
                    Voxels[index].Value = value;
                }
                else
                    _deferredWrites.Enqueue((_generation, index, state, value));
            }
            if (written)
                _externalChange = true;
        }

        /// <summary>
        /// Tells the chunk that something else has written into its <see cref="Voxels"/> (a chunk processor working on
        /// a neighbour, say), so it re-checks whether it has anything to show and re-meshes. Safe from any thread.
        /// </summary>
        public void NotifyVoxelsChanged()
        {
            _externalChange = true;
        }

        // (A plain reference test: this is read for every chunk every frame, and Unity's == costs more)
        internal bool UsesMeshJobs => !ReferenceEquals(_volume, null) && _volume.UseMeshJobs;

        /// <summary>Waiting for a data job, which the volume starts (nearest chunks first).</summary>
        internal bool WantsData => _isDataDirty && !_isGeneratingData;

        /// <summary>Has voxel data that needs (re-)meshing and nothing in flight; the volume schedules it.</summary>
        internal bool WantsMeshJob => _isMeshDirty && _dataReady && !_isGeneratingData && _meshJob == null && UsesMeshJobs &&
                                      _remeshAfter <= _volume.FrameTime;

        /// <summary>Data for the current position is generated and settled (other chunks wait on this to mesh).</summary>
        internal bool IsDataSettled => _dataReady && !_isGeneratingData && !_isDataDirty;

        /// <summary>A mesh job is in flight, or finished and waiting to be applied.</summary>
        internal bool HasMeshJob => _meshJob != null;

        /// <summary>A mesh job has finished and its results can be applied.</summary>
        internal bool HasFinishedMeshJob => _meshJob != null && _meshJob.Handle.IsCompleted;

        internal void StartDataJob()
        {
            _isDataDirty = false;
            _isGeneratingData = true;
            int generation = _generation;
            (int x, int y, int z) pos = Position;
            _volume.BeginDataJob();
            PicaVoxelStats.DataJobs++;

            if (!ThreadPool.QueueUserWorkItem(delegate { GenerateData(generation, pos); }))
            {
                _isGeneratingData = false;
                _isDataDirty = true;
                _volume.EndDataJob();
            }
        }

        public void CheckGeneration()
        {
            if (!_hasData && _rendererOn)
                SetRenderer(false);

            // Persisted edits that arrived on another thread, once this position's data is in place
            if (Volatile.Read(ref _pendingLoadCount) > 0 && _dataReady && !_isGeneratingData)
            {
                while (_pendingLoads.TryDequeue(out var load))
                {
                    Interlocked.Decrement(ref _pendingLoadCount);
                    if (load.generation == _generation)
                        ApplyChanges(load.data);
                }
            }

            if (_externalChange && !_isGeneratingData)
            {
                _externalChange = false;
                if (_dataReady)
                {
                    EvaluateData();
                    if (_hasData)
                        _isMeshDirty = true;
                }
            }

            // Chunks meshed in a job have their data jobs started by the volume, nearest first; the rest start here
            if (!UsesMeshJobs && WantsData)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty data");
                StartDataJob();
            }

            if (_notifyNeighbours)
            {
                _notifyNeighbours = false;
                NotifyNeighbours();
            }

            if (status == ChunkStatus.Ready)
            {
                status = ChunkStatus.NoChange;
                // Discard results from a job started before this chunk was recycled, or one that failed
                if (_meshJobGeneration == _generation && !_meshJobFailed)
                {
                    // Apply what we have even if another re-mesh is pending; it'll be replaced shortly, and it's
                    // better than showing nothing (or a stale mesh) in the meantime.
                    SetMesh();
                }
            }

            if (!UsesMeshJobs && status == ChunkStatus.NoChange && _isMeshDirty && _dataReady && !_isGeneratingData)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty mesh");
                _isMeshDirty = false;
                GenerateMesh(false);
            }

            if (_disableMrNextFrame)
            {
                SetRenderer(false);
                SetCollider(false);
                _disableMrNextFrame= false;
            }

            if (updateColliderNextFrame)
            {
                UpdateCollider();
            }

            if (_baking && _bakeHandle.IsCompleted)
                FinishBake();
        }

        /// <summary>
        /// Flags the neighbouring chunks for re-meshing, since their meshes depend on our voxels: the 6 face-adjacent
        /// ones for their culled border faces, and (Culled meshing) the edge and corner ones too, whose self-shading
        /// samples ours. Any neighbour that meshed before our data was in is out of date. Main thread only.
        /// </summary>
        private void NotifyNeighbours()
        {
            if (!UsesMeshJobs)
            {
                Volume.GetChunk((Position.x - 1, Position.y, Position.z))?.SetMeshDirty();
                Volume.GetChunk((Position.x + 1, Position.y, Position.z))?.SetMeshDirty();
                Volume.GetChunk((Position.x, Position.y - 1, Position.z))?.SetMeshDirty();
                Volume.GetChunk((Position.x, Position.y + 1, Position.z))?.SetMeshDirty();
                Volume.GetChunk((Position.x, Position.y, Position.z - 1))?.SetMeshDirty();
                Volume.GetChunk((Position.x, Position.y, Position.z + 1))?.SetMeshDirty();
                return;
            }

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                Volume.GetChunk((Position.x + dx, Position.y + dy, Position.z + dz))?.SetMeshDirtySoon();
            }
        }

        /// <summary>
        /// Snapshot neighbour chunk references on the main thread, so mesh jobs never touch the Chunks dictionary
        /// from a worker thread while the volume is adding/removing chunks.
        /// </summary>
        private void GatherNeighbours()
        {
            for(int x=0;x<3;x++)
                for(int y=0;y<3;y++)
                    for(int z=0;z<3;z++)
                        _neighbours[x + 3 * (y + 3 * z)] = Volume.GetChunk((Position.x+(x-1),Position.y+(y-1),Position.z+(z-1)));
        }

        /// <summary>
        /// Whether every existing neighbour has finished generating, so a mesh made now won't need redoing as soon
        /// as they do. Main thread only.
        /// </summary>
        internal bool NeighboursSettled()
        {
            GatherNeighbours();
            for (int i = 0; i < 27; i++)
            {
                Chunk n = _neighbours[i];
                if (ReferenceEquals(n, null))
                {
                    // Nothing there yet, but one is on its way: wait for it too, rather than mesh now and again
                    // when it lands
                    int x = i % 3, y = (i / 3) % 3, z = i / 9;
                    if (_volume.IsCreatePending((Position.x + x - 1, Position.y + y - 1, Position.z + z - 1)))
                        return false;
                    continue;
                }
                if (!ReferenceEquals(n, this) && !n.IsDataSettled)
                    return false;
            }
            return true;
        }

        public void GenerateMesh(bool immediate)
        {
            if (UsesMeshJobs)
            {
                if (immediate)
                    MeshNow();
                else
                    _isMeshDirty = true; // the volume schedules a job for it
                return;
            }

            // Can't mesh while a background job is using the shared vertex lists, or while voxel data is still
            // being generated (we'd mesh half-written data). Defer; CheckGeneration will pick it up.
            if (status != ChunkStatus.NoChange || _isGeneratingData || !_dataReady)
            {
                _isMeshDirty = true;
                return;
            }

            EnsureManagedLists();
            GatherNeighbours();

            if (immediate)
            {
                GenerateMeshActual(Volume.MeshingMode);
                SetRenderer(_hasData);
                if (_hasData)
                {
                    bool hasMesh = SetMesh();
                    if (hasMesh && Volume.CollisionMode != CollisionMode.None)
                    {
                        if(Volume.MeshColliderMeshingMode!=Volume.MeshingMode)
                            GenerateMeshActual(Volume.MeshColliderMeshingMode);
                        UpdateCollider();
                    }
                }
                else
                {
                    SetCollider(false);
                }
            }
            else
            {
                // Mark busy *before* queueing so a second job can't be started for this chunk in the meantime
                status = ChunkStatus.CalculatingMesh;
                _meshJobGeneration = _generation;
                _meshJobFailed = false;
                MeshingMode meshMode = Volume.MeshingMode;

                if (!ThreadPool.QueueUserWorkItem(delegate { GenerateMeshThreaded(meshMode); }))
                {
                    status = ChunkStatus.NoChange;
                    _isMeshDirty = true;
                }
            }
        }

        // Culled meshing ///////////////////////////////////////////////

        private void RunBeforeMeshingProcessors()
        {
            foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
            {
                if (p.Schedule == ProcessingSchedule.BeforeMeshing)
                    p.ProcessChunk(_volume, this);
            }
        }

        /// <summary>
        /// Starts a mesh job for this chunk in the given context. The volume calls this, nearest chunks first.
        /// </summary>
        internal void ScheduleMeshJob(MeshJobContext ctx)
        {
            _isMeshDirty = false;
            NeighbourWaitFrames = 0;
            // Chunk processors ran on the meshing thread before; jobs can't call them, so they run here first
            RunBeforeMeshingProcessors();
            GatherNeighbours();
            ctx.Generation = _generation;
            ctx.Pin(_neighbours);
            ctx.Handle = ctx.CreateJob(_volume, _volume.CollisionMode != CollisionMode.None, FloorSolid).Schedule();
            ctx.Scheduled = true;
            _meshJob = ctx;
            PicaVoxelStats.MeshJobs++;
        }

        /// <summary>
        /// Applies a finished mesh job (unless the chunk was recycled meanwhile) and hands its context back.
        /// </summary>
        internal void FinishMeshJob()
        {
            MeshJobContext ctx = _meshJob;
            _meshJob = null;
            ctx.Complete();
            if (ctx.Generation == _generation)
            {
                ApplyMesh(ctx, false);
                PicaVoxelStats.MeshesApplied++;
            }
            _volume.ReturnMeshContext(ctx);
        }

        // Meshes on the main thread now (an edit, say), so the change shows and collides this frame
        private void MeshNow()
        {
            if (_isGeneratingData || !_dataReady)
            {
                _isMeshDirty = true;
                return;
            }

            // Whatever is in flight was made from older voxels
            if (_meshJob != null)
            {
                MeshJobContext old = _meshJob;
                _meshJob = null;
                old.Complete();
                _volume.ReturnMeshContext(old);
            }

            _isMeshDirty = false;
            RunBeforeMeshingProcessors();
            GatherNeighbours();
            MeshJobContext ctx = _volume.ImmediateMeshContext;
            ctx.Generation = _generation;
            ctx.Pin(_neighbours);
            ctx.CreateJob(_volume, _volume.CollisionMode != CollisionMode.None, FloorSolid).Run();
            ctx.Complete();
            ApplyMesh(ctx, true);
            PicaVoxelStats.ImmediateMeshes++;
        }

        private void ConfigureRenderer()
        {
            if (_rendererConfigured) return;
            _rendererConfigured = true;
            mr.shadowCastingMode = Volume.CastShadows;
            mr.receiveShadows = Volume.ReceiveShadows;
            mr.sharedMaterial = Volume.Material;
#if UNITY_EDITOR
            mr.gameObject.isStatic = Volume.gameObject.isStatic;
#endif
        }

        private void ApplyMesh(MeshJobContext ctx, bool immediate)
        {
            // What this chunk now lets the camera see through (cave culling), even when it has no faces to show
            _volume.SetConnectivity(this, (ushort)ctx.Connectivity[0]);
            Mesh mesh = mf.sharedMesh;
            int vertexCount = ctx.Vertices.Length;
            if (vertexCount == 0)
            {
                // Don't leave a previous mesh/collider behind (e.g. last block removed, or fully enclosed chunk)
                mesh.Clear();
                SetRenderer(false);
                SetCollider(false);
                return;
            }

            ConfigureRenderer();

            float3 min = ctx.Bounds[0];
            float3 max = ctx.Bounds[1];
            var bounds = new Bounds((min + max) * 0.5f, max - min);

            mesh.SetVertexBufferParams(vertexCount, ChunkVertex.Layout);
            mesh.SetVertexBufferData(ctx.Vertices.AsArray(), 0, 0, vertexCount, 0, UploadFlags);
            int indexCount = ctx.Indices.Length;
            if (ctx.Indices16.Length == indexCount)
            {
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
                mesh.SetIndexBufferData(ctx.Indices16.AsArray(), 0, 0, indexCount, UploadFlags);
            }
            else
            {
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
                mesh.SetIndexBufferData(ctx.Indices.AsArray(), 0, 0, indexCount, UploadFlags);
            }
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount)
            {
                bounds = bounds,
                firstVertex = 0,
                vertexCount = vertexCount,
            }, UploadFlags);
            mesh.bounds = bounds;

            SetRenderer(_hasData);

            if (Volume.CollisionMode == CollisionMode.None || ctx.CollisionVertices.Length == 0)
            {
                SetCollider(false);
                return;
            }

            // Fill the collision mesh that isn't on the collider; if it's still cooking an older result, finish that
            int target = _colActive == 0 ? 1 : 0;
            if (_baking)
            {
                _bakeHandle.Complete();
                _baking = false;
                if (_bakeIndex != target)
                    FinishBakeAssign();
            }
            Mesh col = _colMeshes[target];
            if (col == null)
            {
                col = new Mesh { name = "PicaVoxel Collision" };
                _colMeshes[target] = col;
            }

            int colVerts = ctx.CollisionVertices.Length;
            int colIndices = ctx.CollisionIndices.Length;
            col.SetVertexBufferParams(colVerts, ChunkVertex.CollisionLayout);
            col.SetVertexBufferData(ctx.CollisionVertices.AsArray(), 0, 0, colVerts, 0, UploadFlags);
            col.SetIndexBufferParams(colIndices, IndexFormat.UInt32);
            col.SetIndexBufferData(ctx.CollisionIndices.AsArray(), 0, 0, colIndices, UploadFlags);
            col.subMeshCount = 1;
            col.SetSubMesh(0, new SubMeshDescriptor(0, colIndices)
            {
                bounds = bounds,
                firstVertex = 0,
                vertexCount = colVerts,
            }, UploadFlags);
            col.bounds = bounds;

            if (immediate)
            {
                // An edit: cook now (the merged mesh is small) so the change collides this frame
                AssignCollider(target);
                return;
            }

            _bakeIndex = target;
            _bakeGeneration = _generation;
            _bakeHandle = new BakeColliderJob
            {
                MeshId = col.GetInstanceID(),
                Convex = Volume.CollisionMode == CollisionMode.MeshColliderConvex,
                Options = mc.cookingOptions,
            }.Schedule();
            _baking = true;
        }

        private void FinishBake()
        {
            _bakeHandle.Complete();
            _baking = false;
            FinishBakeAssign();
        }

        private void FinishBakeAssign()
        {
            if (_bakeGeneration != _generation || _bakeIndex < 0)
                return;
            AssignCollider(_bakeIndex);
            _bakeIndex = -1;
            PicaVoxelStats.ColliderBakes++;
        }

        private void AssignCollider(int index)
        {
            if (!_hasData)
            {
                SetCollider(false);
                return;
            }
            bool convex = Volume.CollisionMode == CollisionMode.MeshColliderConvex;
            if (mc.convex != convex)
                mc.convex = convex;
            mc.sharedMesh = _colMeshes[index];
            _colActive = index;
            SetCollider(true);
        }

        private void SetRenderer(bool on, bool force = false)
        {
            if (mr is null || (!force && _rendererOn == on)) return;
            _rendererOn = on;
            ApplyRenderer();
        }

        /// <summary>True while cave culling has this chunk hidden (see Volume.CaveCulling). Its collider stays on.</summary>
        public bool CaveCulled { get; private set; }
        private bool _shadowsOnly;

        internal void SetCaveCulled(bool culled)
        {
            if (CaveCulled == culled) return;
            CaveCulled = culled;
            ApplyRenderer();
        }

        // The renderer shows when the chunk has a mesh to show and cave culling hasn't hidden it. A volume that casts
        // shadows keeps a culled chunk drawing into the shadow maps (shadows only), so its shadows don't vanish.
        private void ApplyRenderer()
        {
            if (mr is null) return;
            bool shadows = !ReferenceEquals(_volume, null) && _volume.CastShadows != ShadowCastingMode.Off;
            bool show = _rendererOn && (!CaveCulled || shadows);
            if (mr.enabled != show)
                mr.enabled = show;
            if (shadows && _rendererConfigured)
            {
                bool shadowsOnly = _rendererOn && CaveCulled;
                if (shadowsOnly != _shadowsOnly)
                {
                    _shadowsOnly = shadowsOnly;
                    mr.shadowCastingMode = shadowsOnly ? ShadowCastingMode.ShadowsOnly : _volume.CastShadows;
                }
            }
        }

        // The world below the bottom row of chunks is solid (see Volume.CullWorldFloor)
        private bool FloorSolid => _volume.CullWorldFloor && _volume.IsInfinite &&
                                   _volume.InfiniteChunkBounds.y > 0 && Position.y == -_volume.InfiniteChunkBounds.y;

        private void SetCollider(bool on)
        {
            if (mc is null || _colliderOn == on) return;
            mc.enabled = on;
            _colliderOn = on;
        }

        private void OnDestroy()
        {
            if (_baking)
            {
                _bakeHandle.Complete();
                _baking = false;
            }
            if (_meshJob != null)
            {
                _meshJob.Complete();
                if (!ReferenceEquals(_volume, null))
                    _volume.ReturnMeshContext(_meshJob);
                _meshJob = null;
            }
            for (int i = 0; i < _colMeshes.Length; i++)
            {
                DestroyMesh(_colMeshes[i]);
                _colMeshes[i] = null;
            }
            if (_ownsRenderMesh && mf != null)
                DestroyMesh(mf.sharedMesh);
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }

        /////////////////////////////////////////////////////////////////

        private void EnsureManagedLists()
        {
            if (vertices != null) return;
            vertices = new List<Vector3>(2048);
            uvs = new List<Vector4>(2048);
            colors = new List<Color32>(2048);
            indexes = new List<int>(2048);
        }

        private void GenerateMeshThreaded(MeshingMode meshMode)
        {
            try
            {
                GenerateMeshActual(meshMode);
            }
            catch (Exception ex)
            {
                // Previously an exception here left status stuck at CalculatingMesh, so the chunk never meshed again
                _meshJobFailed = true;
                Debug.LogException(ex);
            }
            finally
            {
                status = ChunkStatus.Ready;
            }
        }

        private Chunk[] _neighbours = new Chunk[27];
        private void GenerateMeshActual(MeshingMode meshMode)
        {
            foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
            {
                if (p.Schedule == ProcessingSchedule.BeforeMeshing)
                    p.ProcessChunk(_volume, this);
            }

            switch (meshMode)
            {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref Voxels, this, _neighbours, Volume.CustomBlocksDict, Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
            }
        }

        /// <summary>
        /// Applies the generated vertex lists to the chunk's mesh (Greedy and Marching meshers). Returns false if
        /// there was nothing to show.
        /// </summary>
        private bool SetMesh()
        {
            if (vertices.Count == 0)
            {
                // Don't leave a previous mesh/collider behind (e.g. last block removed, or fully enclosed chunk)
                mf.sharedMesh.Clear();
                SetRenderer(false);
                SetCollider(false);
                updateColliderNextFrame = false;
                return false;
            }

            ConfigureRenderer();

            mf.sharedMesh.Clear();
            mf.sharedMesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mf.sharedMesh.SetVertices(vertices);
            mf.sharedMesh.SetColors(colors);
            mf.sharedMesh.SetUVs(0, uvs);
            mf.sharedMesh.SetTriangles(indexes, 0);
            mf.sharedMesh.RecalculateNormals();

            if (Volume.CollisionMode != CollisionMode.None)
            {
                updateColliderNextFrame = true;
            }

            SetRenderer(_hasData);
            return true;
        }

        private void UpdateCollider()
        {
            updateColliderNextFrame = false;
            if (!_hasData || mf.sharedMesh.vertexCount == 0)
            {
                SetCollider(false);
                return;
            }

            mf.sharedMesh.RecalculateBounds();
            // Re-assigning the same Mesh instance doesn't reliably rebake the collider, so clear it first
            mc.sharedMesh = null;
            mc.sharedMesh = mf.sharedMesh;
            SetCollider(true);
        }

        public void SetMeshDirty()
        {
            if (!_hasData)
                return;
            _isMeshDirty = true;
            _remeshAfter = 0f;
        }

        // A neighbour's data arrived or changed: re-mesh, but if this chunk is already showing a mesh, after a short
        // wait, so neighbours landing one after another (the volume adds them a slice at a time) cost one re-mesh
        // rather than one each. Edits use SetMeshDirty, so the chunks around them update at once.
        internal void SetMeshDirtySoon()
        {
            if (!_hasData)
                return;
            if (!_isMeshDirty && _rendererOn)
                _remeshAfter = Time.unscaledTime + _volume.NeighbourRemeshDelay;
            _isMeshDirty = true;
        }

        private void EvaluateData()
        {
            _hasData = false;
            for (int z = 0; z < Volume.ChunkSize; z++)
                for (int y = 0; y < Volume.ChunkSize; y++)
                    for (int x = 0; x < Volume.ChunkSize; x++)
                    {
                        if (!_hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                        {
                            _hasData = true;
                            return;
                        }
                    }
        }

        /// <summary>
        /// Applies persisted edits (as written by SaveChanges). Persisters may call this from any thread: off the main
        /// thread the edits are queued and applied by the next CheckGeneration, once this position's data is ready.
        /// </summary>
        public void LoadChanges(byte[] data)
        {
            if (data == null)
                return;
            if (!Volume.IsMainThread)
            {
                _pendingLoads.Enqueue((_generation, data));
                Interlocked.Increment(ref _pendingLoadCount);
                return;
            }
            ApplyChanges(data);
        }

        private void ApplyChanges(byte[] data)
        {
            byte[] vbuff = new byte[Voxel.BYTE_SIZE];

            using (MemoryStream ms = new MemoryStream(data))
            {
                using (BinaryReader br = new BinaryReader(ms))
                {
                    while (ms.Position < ms.Length)
                    {
                        int x = br.ReadInt32();
                        int y = br.ReadInt32();
                        int z = br.ReadInt32();

                        int l = ms.Read(vbuff, 0, Voxel.BYTE_SIZE);
                        if (l == Voxel.BYTE_SIZE)
                        {
                            Voxel v = new Voxel(vbuff);
                            Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)] = v;
                            _changes[(x, y, z)] = v;
                        }

                    }
                }
            }

            EvaluateData();
            _isMeshDirty = true;
            // Loaded edits may touch our borders
            _notifyNeighbours = true;
        }

        private bool SaveChanges()
        {
            if (!_volume.IsPersisterReady)
                return false;

            byte[] outbytes;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    foreach(KeyValuePair<(int x, int y, int z), Voxel> change in _changes)
                    {
                        bw.Write(change.Key.x);
                        bw.Write(change.Key.y);
                        bw.Write(change.Key.z);
                        bw.Write(change.Value.ToBytes());
                    }
                    bw.Flush();
                }

                outbytes = ms.ToArray();
            }

            return _volume.SaveChunkChanges(Position, outbytes);
        }
    }
}
