using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using PicaVoxel;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxelPerf
{
    /// <summary>
    /// The Burst Culled mesher against the managed one it replaces, on the same random voxels: the render mesh must
    /// match vertex for vertex, and the merged collision mesh must answer raycasts as the old collider did.
    /// </summary>
    public class CulledMeshEquivalenceTests
    {
        const int CS = 16;
        Volume _volume;
        Camera _camera;
        readonly Dictionary<(int, int, int), Chunk> _chunks = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            _camera = camGo.AddComponent<Camera>();

            var go = new GameObject("Volume");
            go.SetActive(false);
            _volume = go.AddComponent<Volume>();
#if UNITY_EDITOR
            _volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab");
#endif
            _volume.ChunkSize = CS;
            _volume.VoxelSize = 1f;
            _volume.DelayGeneration = true; // no Update work: the test drives the meshers itself
            _volume.MeshingMode = MeshingMode.Culled;
            _volume.CollisionMode = CollisionMode.MeshColliderConcave;

            // An off-centre, non-symmetric mesh so orientation errors show
            var custom = new Mesh();
            custom.SetVertices(new List<Vector3>
            {
                new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.2f, -0.5f), new(-0.5f, 0.2f, 0.3f),
                new(0.1f, 0.5f, 0.4f), new(-0.3f, -0.5f, 0.5f),
            });
            custom.SetUVs(0, new List<Vector2> { new(0, 0), new(1, 0), new(1, 0.6f), new(0, 0.6f), new(0.55f, 1), new(0.2f, 0.1f) });
            custom.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 3, 4, 2, 0, 5, 3, 5, 4, 3, 1, 2, 4 }, 0);

            _volume.CustomBlocks = new[]
            {
                new CustomBlock { VoxelValue = 5, AllowOrientation = true, Mesh = custom },
                new CustomBlock { VoxelValue = 6, AllowOrientation = true, HasTransparency = true, Mesh = custom },
                new CustomBlock { VoxelValue = 7, HasTransparency = true },
                new CustomBlock { VoxelValue = 9 }, // opaque custom without a mesh: a two-sided cube in pass 0
                new CustomBlock { VoxelValue = 12, HasTransparency = true },
                // Water (not in the random fills, which the managed mesher must match: it doesn't know these options)
                new CustomBlock { VoxelValue = 16, HasTransparency = true, HideFacesAgainstSame = true },
                // ...and a block with no collision at all
                new CustomBlock { VoxelValue = 17, HasTransparency = true, HideFacesAgainstSame = true, NoCollision = true },
            };
            go.AddComponent<NullPersister>();
            go.SetActive(true);
            yield return null; // Start builds CustomBlocksDict
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var c in _chunks.Values)
                if (c != null) Object.Destroy(c.gameObject);
            _chunks.Clear();
            Object.Destroy(_volume.gameObject);
            Object.Destroy(_camera.gameObject);
            yield return null;
        }

        void MakeChunks(int seed, float fill, bool dropSomeNeighbours)
        {
            foreach (var c in _chunks.Values)
                if (c != null) Object.Destroy(c.gameObject);
            _chunks.Clear();
            _volume.Chunks.Clear();

            var rng = new System.Random(seed);
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                if (dropSomeNeighbours && (x, y, z) != (0, 0, 0) && rng.NextDouble() < 0.3)
                    continue;
                var chunk = Object.Instantiate(_volume.ChunkPrefab, _volume.transform, false).GetComponent<Chunk>();
                chunk.Initialize((x, y, z), _volume);
                for (int i = 0; i < chunk.Voxels.Length; i++)
                    chunk.Voxels[i] = RandomVoxel(rng, fill);
                _chunks[(x, y, z)] = chunk;
                _volume.Chunks[(x, y, z)] = chunk;
            }
        }

        static Voxel RandomVoxel(System.Random rng, float fill)
        {
            if (rng.NextDouble() > fill)
                return new Voxel { State = 0 };
            var color = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(200, 256));
            double r = rng.NextDouble();
            if (r < 0.70) return new Voxel { State = 1, Value = (byte)rng.Next(0, 4), Color = color };
            if (r < 0.78) return new Voxel { State = 2, Value = 12, Color = color };               // leaves
            if (r < 0.83) return new Voxel { State = (byte)rng.Next(1, 3), Value = 7, Color = color }; // glass
            if (r < 0.90) return new Voxel { State = (byte)rng.Next(1, 6), Value = 5, Color = color }; // oriented mesh
            if (r < 0.94) return new Voxel { State = (byte)rng.Next(2, 6), Value = 6, Color = color }; // transparent mesh
            if (r < 0.97) return new Voxel { State = 1, Value = 9, Color = color };                // opaque no-mesh custom
            return new Voxel { State = 2, Value = 1, Color = color };                              // "hidden" plain block
        }

        Chunk[] Neighbours()
        {
            var nbs = new Chunk[27];
            for (int z = 0; z < 3; z++)
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                nbs[x + 3 * (y + 3 * z)] = _chunks.GetValueOrDefault((x - 1, y - 1, z - 1));
            return nbs;
        }

        static readonly (int seed, float fill, bool drop, float shade)[] Cases =
        {
            (1, 0.5f, false, 0.7f), (2, 0.9f, false, 0.7f), (3, 0.2f, true, 0.7f), (4, 0.5f, true, 0f),
            (5, 0.97f, true, 0.4f), (6, 0.05f, false, 0.7f),
        };

        [UnityTest]
        public IEnumerator RenderMeshMatchesManagedMesher()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            try
            {
                foreach (var c in Cases)
                {
                    MakeChunks(c.seed, c.fill, c.drop);
                    _volume.SelfShadingIntensity = c.shade;
                    Chunk centre = _chunks[(0, 0, 0)];
                    Chunk[] nbs = Neighbours();

                    var verts = new List<Vector3>();
                    var uvs = new List<Vector4>();
                    var colors = new List<Color32>();
                    var indices = new List<int>();
                    Voxel[] voxels = centre.Voxels;
                    MeshGenerator.GenerateCulled(verts, uvs, colors, indices, ref voxels, centre, nbs, _volume.CustomBlocksDict,
                        1f, 0f, 0, 0, 0, CS, CS, CS, CS - 1, CS - 1, CS - 1, c.shade);
                    var oldMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                    oldMesh.SetVertices(verts);
                    oldMesh.SetTriangles(indices, 0);
                    oldMesh.RecalculateNormals();
                    Vector3[] oldNormals = oldMesh.normals;

                    ctx.Pin(nbs);
                    Job(ctx, tables, c.shade, true).Run();
                    ctx.Complete();

                    Assert.AreEqual(verts.Count, ctx.Vertices.Length, $"case {c.seed}: vertex count");
                    Assert.AreEqual(indices.Count, ctx.Indices.Length, $"case {c.seed}: index count");
                    int colorOff = 0, colorOffBy2 = 0;
                    for (int i = 0; i < verts.Count; i++)
                    {
                        ChunkVertex v = ctx.Vertices[i];
                        Assert.Less(Vector3.Distance(verts[i], v.Position), 1e-4f, $"case {c.seed}: position {i}");
                        Assert.Less(Vector4.Distance(uvs[i], (Vector4)v.UV), 1e-5f, $"case {c.seed}: uv {i}");
                        Assert.Less(Vector3.Distance(oldNormals[i], v.Normal), 1e-3f, $"case {c.seed}: normal {i} {oldNormals[i]} vs {v.Normal}");
                        int d = Mathf.Max(Mathf.Abs(colors[i].r - v.Color.r), Mathf.Abs(colors[i].g - v.Color.g),
                            Mathf.Abs(colors[i].b - v.Color.b), Mathf.Abs(colors[i].a - v.Color.a));
                        if (d > 0) colorOff++;
                        if (d > 1) colorOffBy2++;
                    }
                    for (int i = 0; i < indices.Count; i++)
                        Assert.AreEqual(indices[i], ctx.Indices[i], $"case {c.seed}: index {i}");
                    Assert.AreEqual(0, colorOffBy2, $"case {c.seed}: colours off by more than 1");
                    Debug.Log($"[Equivalence] case {c.seed}: {verts.Count} vertices, {indices.Count / 3} triangles match; " +
                              $"{colorOff} colours off by one; collision {ctx.CollisionIndices.Length / 3} triangles " +
                              $"(render {indices.Count / 3})");
                    Object.Destroy(oldMesh);
                    yield return null;
                }
            }
            finally
            {
                ctx.Dispose();
                tables.Dispose();
            }
        }

        // A lake: a block of water draws only its outside (both ways, so it shows from under the surface too), not
        // the faces between its own blocks, and only that outside collides. A No Collision block leaves nothing in
        // the collision mesh, while the lake bed under it still collides.
        [UnityTest]
        public IEnumerator WaterDrawsAndCollidesOnlyOnItsOutside()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            try
            {
                MakeChunks(1, 0f, false);   // all air
                Chunk centre = _chunks[(0, 0, 0)];
                for (int z = 4; z < 10; z++) for (int y = 5; y < 9; y++) for (int x = 3; x < 9; x++)
                    centre.Voxels[x + CS * (y + CS * z)] = new Voxel { State = 1, Value = 16, Color = new Color32(255, 255, 255, 255) };

                ctx.Pin(Neighbours());
                Job(ctx, tables, 0.7f, true).Run();
                ctx.Complete();
                int waterQuads = 0;
                for (int i = 0; i < ctx.Vertices.Length; i += 4)
                    if (Mathf.RoundToInt(ctx.Vertices[i].UV.z) == 16) waterQuads++;
                // A 6 x 4 x 6 block: 2 * (6*4 + 6*6 + 4*6) = 168 outside faces, each drawn both ways
                Assert.AreEqual(336, waterQuads, "water faces");
                // Its collision is its outside: six merged rectangles (each face both ways), 2 triangles each
                Assert.AreEqual(6 * 2 * 2 * 3, ctx.CollisionIndices.Length, "water's collision indices");

                // The same block with No Collision: drawn the same, nothing to collide with
                for (int z = 4; z < 10; z++) for (int y = 5; y < 9; y++) for (int x = 3; x < 9; x++)
                    centre.Voxels[x + CS * (y + CS * z)].Value = 17;
                ctx.Pin(Neighbours());
                Job(ctx, tables, 0.7f, true).Run();
                ctx.Complete();
                Assert.AreEqual(0, ctx.CollisionIndices.Length, "a No Collision block collides");

                // On a stone floor: the floor collides, the block's underside is hidden by it
                for (int z = 4; z < 10; z++) for (int x = 3; x < 9; x++)
                    centre.Voxels[x + CS * (4 + CS * z)] = new Voxel { State = 1, Value = 2, Color = new Color32(255, 255, 255, 255) };
                ctx.Pin(Neighbours());
                Job(ctx, tables, 0.7f, true).Run();
                ctx.Complete();
                waterQuads = 0;
                for (int i = 0; i < ctx.Vertices.Length; i += 4)
                    if (Mathf.RoundToInt(ctx.Vertices[i].UV.z) == 17) waterQuads++;
                Assert.AreEqual(336 - 72, waterQuads, "faces over a floor");
                Assert.Greater(ctx.CollisionIndices.Length, 0, "the floor collides");
            }
            finally
            {
                ctx.Dispose();
                tables.Dispose();
            }
            yield return null;
        }

        // Which sides of a chunk its open voxels join, against a plain flood fill written here (and a pair numbering
        // counted out in order, rather than the job's formula)
        static int ReferenceConnectivity(Chunk chunk, Dictionary<int, CustomBlockData> blocks)
        {
            var open = new bool[CS * CS * CS];
            for (int i = 0; i < open.Length; i++)
            {
                Voxel v = chunk.Voxels[i];
                open[i] = v.State != 1 || (blocks.TryGetValue(v.Value, out var cb) && (cb.HasTransparency || cb.HasMesh));
            }
            int mask = 0;
            var seen = new bool[open.Length];
            for (int start = 0; start < open.Length; start++)
            {
                if (!open[start] || seen[start]) continue;
                int touched = 0;
                var stack = new Stack<int>();
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % CS, y = i / CS % CS, z = i / (CS * CS);
                    if (x == 0) touched |= 1 << 0;
                    if (x == CS - 1) touched |= 1 << 1;
                    if (y == 0) touched |= 1 << 2;
                    if (y == CS - 1) touched |= 1 << 3;
                    if (z == 0) touched |= 1 << 4;
                    if (z == CS - 1) touched |= 1 << 5;
                    foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) })
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= CS || ny >= CS || nz >= CS) continue;
                        int n = nx + CS * (ny + CS * nz);
                        if (open[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                    }
                }
                int bit = 0;
                for (int a = 0; a < 5; a++)
                for (int b = a + 1; b < 6; b++, bit++)
                    if ((touched & (1 << a)) != 0 && (touched & (1 << b)) != 0)
                        mask |= 1 << bit;
            }
            return mask;
        }

        static Voxel Solid() => new Voxel { State = 1, Value = 2, Color = new Color32(255, 255, 255, 255) };

        [UnityTest]
        public IEnumerator ConnectivityMaskMatchesAReferenceFloodFill()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            try
            {
                var shapes = new List<(string name, System.Action<Chunk> fill, int expect)>
                {
                    ("all air", c => { }, ChunkConnectivity.All),
                    ("all solid", c => { for (int i = 0; i < c.Voxels.Length; i++) c.Voxels[i] = Solid(); }, 0),
                    // A tunnel along x: -x and +x only (pair 0)
                    ("tunnel", c => { Fill(c, Solid()); for (int x = 0; x < CS; x++) c.Voxels[x + CS * (5 + CS * 7)] = default; }, 1 << 0),
                    // From the -x side along x to the middle, then along z to the +z side: (-x, +z) is pair 4
                    ("L tunnel", c =>
                    {
                        Fill(c, Solid());
                        for (int x = 0; x <= 8; x++) c.Voxels[x + CS * (5 + CS * 8)] = default;
                        for (int z = 8; z < CS; z++) c.Voxels[8 + CS * (5 + CS * z)] = default;
                    }, 1 << 4),
                    // A closed cavity touches no side
                    ("sealed cavity", c =>
                    {
                        Fill(c, Solid());
                        for (int z = 4; z < 9; z++) for (int y = 4; y < 9; y++) for (int x = 4; x < 9; x++) c.Voxels[x + CS * (y + CS * z)] = default;
                    }, 0),
                    // Two separate tunnels (x to x, and y to y): they don't join each other
                    ("two tunnels", c =>
                    {
                        Fill(c, Solid());
                        for (int x = 0; x < CS; x++) c.Voxels[x + CS * (3 + CS * 3)] = default;
                        for (int y = 0; y < CS; y++) c.Voxels[12 + CS * (y + CS * 12)] = default;
                    }, (1 << 0) | (1 << ChunkConnectivity.PairBit(2, 3))),
                    // A see-through block (leaves, value 12) is open; a glass-like meshed block (5) too; solid cubes are not
                    ("leaves", c =>
                    {
                        Fill(c, Solid());
                        for (int x = 0; x < CS; x++) c.Voxels[x + CS * (2 + CS * 2)] = new Voxel { State = 2, Value = 12 };
                    }, 1 << 0),
                };

                foreach (var (name, fill, expect) in shapes)
                {
                    MakeChunks(1, 0f, false);
                    Chunk centre = _chunks[(0, 0, 0)];
                    fill(centre);
                    ctx.Pin(Neighbours());
                    Job(ctx, tables, 0.7f, false).Run();
                    ctx.Complete();
                    int reference = ReferenceConnectivity(centre, _volume.CustomBlocksDict);
                    Assert.AreEqual(expect, reference, $"{name}: the reference itself");
                    Assert.AreEqual(expect, ctx.Connectivity[0], $"{name}: mask");
                }

                // Random fills of every density, custom blocks and orientation states among them
                for (int seed = 0; seed < 60; seed++)
                {
                    MakeChunks(500 + seed, 0.05f + 0.016f * seed, false);
                    Chunk centre = _chunks[(0, 0, 0)];
                    ctx.Pin(Neighbours());
                    Job(ctx, tables, 0.7f, false).Run();
                    ctx.Complete();
                    Assert.AreEqual(ReferenceConnectivity(centre, _volume.CustomBlocksDict), ctx.Connectivity[0], $"random fill {seed}");
                }
            }
            finally
            {
                ctx.Dispose();
                tables.Dispose();
            }
            yield return null;
        }

        static void Fill(Chunk c, Voxel v)
        {
            for (int i = 0; i < c.Voxels.Length; i++) c.Voxels[i] = v;
        }

        // The bottom row of a volume with bounds has solid world under it: its lowest voxels' down faces (and their
        // collision) go, and nothing else changes
        [UnityTest]
        public IEnumerator FloorCullingDropsOnlyTheFacesUnderTheBottomRow()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            try
            {
                MakeChunks(1, 0f, false);
                Chunk centre = _chunks[(0, 0, 0)];
                // A slab four voxels deep right on the floor, with a notch cut out of its bottom layer
                for (int z = 0; z < CS; z++) for (int y = 0; y < 4; y++) for (int x = 0; x < CS; x++)
                    centre.Voxels[x + CS * (y + CS * z)] = Solid();
                centre.Voxels[3 + CS * (0 + CS * 3)] = default;

                int Faces(MeshJobContext c, bool onFloor)
                {
                    int n = 0;
                    for (int i = 0; i < c.Vertices.Length; i += 4)
                        if (c.Vertices[i].Normal.y < -0.9f && (c.Vertices[i].Position.y < 0.001f) == onFloor) n++;
                    return n;
                }

                ctx.Pin(Neighbours());
                Job(ctx, tables, 0.7f, true, floorSolid: false).Run();
                ctx.Complete();
                int quadsWith = ctx.Vertices.Length / 4;
                int floorWith = Faces(ctx, true);
                int collisionWith = ctx.CollisionIndices.Length;

                ctx.Pin(Neighbours());
                Job(ctx, tables, 0.7f, true, floorSolid: true).Run();
                ctx.Complete();
                int floorWithout = Faces(ctx, true);

                // 16 x 16 floor faces, less the notch's, drawn before; none after
                Assert.AreEqual(CS * CS - 1, floorWith, "floor faces without culling");
                Assert.AreEqual(0, floorWithout, "floor faces with culling");
                Assert.AreEqual(quadsWith - floorWith, ctx.Vertices.Length / 4, "only floor faces are gone");
                Assert.Less(ctx.CollisionIndices.Length, collisionWith, "the floor's collision is gone too");
                // The notch's own faces (its roof, which faces down but isn't on the floor) are still there
                Assert.AreEqual(1, Faces(ctx, false), "faces facing down above the floor");
            }
            finally
            {
                ctx.Dispose();
                tables.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MergedColliderAnswersRaycastsLikeTheOldOne()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            var created = new List<Object>();
            try
            {
                foreach (var c in Cases)
                {
                    MakeChunks(c.seed, c.fill, c.drop);
                    Chunk centre = _chunks[(0, 0, 0)];
                    Chunk[] nbs = Neighbours();

                    var verts = new List<Vector3>();
                    var uvs = new List<Vector4>();
                    var colors = new List<Color32>();
                    var indices = new List<int>();
                    Voxel[] voxels = centre.Voxels;
                    MeshGenerator.GenerateCulled(verts, uvs, colors, indices, ref voxels, centre, nbs, _volume.CustomBlocksDict,
                        1f, 0f, 0, 0, 0, CS, CS, CS, CS - 1, CS - 1, CS - 1, 0.7f);
                    var oldMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                    oldMesh.SetVertices(verts);
                    oldMesh.SetTriangles(indices, 0);

                    ctx.Pin(nbs);
                    Job(ctx, tables, 0.7f, true).Run();
                    ctx.Complete();
                    var newMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                    newMesh.SetVertices(ToVector3(ctx.CollisionVertices));
                    newMesh.SetTriangles(ToInts(ctx.CollisionIndices), 0);

                    // Far apart, so each ray can be cast at one and then (shifted) at the other
                    var oldGo = new GameObject("old", typeof(MeshCollider));
                    oldGo.GetComponent<MeshCollider>().sharedMesh = oldMesh;
                    var newGo = new GameObject("new", typeof(MeshCollider));
                    newGo.transform.position = new Vector3(1000, 0, 0);
                    newGo.GetComponent<MeshCollider>().sharedMesh = newMesh;
                    created.Add(oldGo); created.Add(newGo); created.Add(oldMesh); created.Add(newMesh);
                    Physics.SyncTransforms();

                    var rng = new System.Random(c.seed * 31);
                    int rays = 4000, hitsOld = 0, mismatch = 0;
                    for (int i = 0; i < rays; i++)
                    {
                        var origin = new Vector3(Rand(rng, -4, 20), Rand(rng, -4, 20), Rand(rng, -4, 20));
                        Vector3 dir = new Vector3(Rand(rng, -1, 1), Rand(rng, -1, 1), Rand(rng, -1, 1)).normalized;
                        if (dir == Vector3.zero) continue;
                        bool a = Physics.Raycast(origin, dir, out RaycastHit ha, 60f);
                        bool b = Physics.Raycast(origin + new Vector3(1000, 0, 0), dir, out RaycastHit hb, 60f);
                        if (a && ha.collider.gameObject != oldGo) a = false;
                        if (b && hb.collider.gameObject != newGo) b = false;
                        if (a) hitsOld++;
                        if (a != b || (a && (Mathf.Abs(ha.distance - hb.distance) > 1e-3f ||
                                             Vector3.Distance(ha.normal, hb.normal) > 1e-3f)))
                            mismatch++;
                    }
                    Debug.Log($"[Equivalence] case {c.seed}: raycasts {rays}, hits {hitsOld}, mismatches {mismatch}; " +
                              $"collider triangles {indices.Count / 3} -> {ctx.CollisionIndices.Length / 3}");
                    // Rays exactly along an edge may go either way; anything more is a real difference
                    Assert.LessOrEqual(mismatch, rays / 1000, $"case {c.seed}: raycast mismatches");

                    foreach (var o in created) Object.Destroy(o);
                    created.Clear();
                    yield return null;
                }
            }
            finally
            {
                foreach (var o in created) if (o != null) Object.Destroy(o);
                ctx.Dispose();
                tables.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator MeshingSpeed()
        {
            var tables = Volume.BuildBlockTables(_volume.CustomBlocksDict);
            var ctx = new MeshJobContext(CS);
            try
            {
                // Terrain-like: solid below a bumpy surface
                MakeChunks(11, 0f, false);
                foreach (var kv in _chunks)
                for (int z = 0; z < CS; z++)
                for (int x = 0; x < CS; x++)
                {
                    int h = 8 + (int)(4 * Mathf.Sin((x + kv.Key.Item1 * CS) * 0.4f) * Mathf.Cos((z + kv.Key.Item3 * CS) * 0.3f));
                    for (int y = 0; y < CS; y++)
                        if (y + kv.Key.Item2 * CS < h)
                            kv.Value.Voxels[x + CS * (y + CS * z)] = new Voxel { State = 1, Value = 1, Color = Color.white };
                }
                Chunk centre = _chunks[(0, 0, 0)];
                Chunk[] nbs = Neighbours();
                var verts = new List<Vector3>(); var uvs = new List<Vector4>(); var colors = new List<Color32>(); var indices = new List<int>();
                Voxel[] voxels = centre.Voxels;

                const int n = 200;
                for (int i = 0; i < 5; i++)
                    MeshGenerator.GenerateCulled(verts, uvs, colors, indices, ref voxels, centre, nbs, _volume.CustomBlocksDict, 1f, 0f, 0, 0, 0, CS, CS, CS, CS - 1, CS - 1, CS - 1, 0.7f);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++)
                    MeshGenerator.GenerateCulled(verts, uvs, colors, indices, ref voxels, centre, nbs, _volume.CustomBlocksDict, 1f, 0f, 0, 0, 0, CS, CS, CS, CS - 1, CS - 1, CS - 1, 0.7f);
                double managed = sw.Elapsed.TotalMilliseconds / n;

                ctx.Pin(nbs);
                for (int i = 0; i < 5; i++) Job(ctx, tables, 0.7f, true).Run();
                sw.Restart();
                for (int i = 0; i < n; i++) Job(ctx, tables, 0.7f, true).Run();
                double burst = sw.Elapsed.TotalMilliseconds / n;
                ctx.Complete();

                // Upload + collider cook, old way (lists + RecalculateNormals + cook render mesh) vs new
                var go = new GameObject("cook", typeof(MeshCollider));
                var mc = go.GetComponent<MeshCollider>();
                var m = new Mesh();
                sw.Restart();
                for (int i = 0; i < 50; i++)
                {
                    m.Clear();
                    m.SetVertices(verts); m.SetColors(colors); m.SetUVs(0, uvs); m.SetTriangles(indices, 0); m.RecalculateNormals();
                    mc.sharedMesh = null; mc.sharedMesh = m;
                }
                double oldApply = sw.Elapsed.TotalMilliseconds / 50;
                var col = new Mesh();
                sw.Restart();
                for (int i = 0; i < 50; i++)
                {
                    m.SetVertexBufferParams(ctx.Vertices.Length, ChunkVertex.Layout);
                    m.SetVertexBufferData(ctx.Vertices.AsArray(), 0, 0, ctx.Vertices.Length);
                    m.SetIndexBufferParams(ctx.Indices16.Length, IndexFormat.UInt16);
                    m.SetIndexBufferData(ctx.Indices16.AsArray(), 0, 0, ctx.Indices16.Length);
                    m.subMeshCount = 1;
                    m.SetSubMesh(0, new SubMeshDescriptor(0, ctx.Indices16.Length));
                    col.SetVertexBufferParams(ctx.CollisionVertices.Length, ChunkVertex.CollisionLayout);
                    col.SetVertexBufferData(ctx.CollisionVertices.AsArray(), 0, 0, ctx.CollisionVertices.Length);
                    col.SetIndexBufferParams(ctx.CollisionIndices.Length, IndexFormat.UInt32);
                    col.SetIndexBufferData(ctx.CollisionIndices.AsArray(), 0, 0, ctx.CollisionIndices.Length);
                    col.subMeshCount = 1;
                    col.SetSubMesh(0, new SubMeshDescriptor(0, ctx.CollisionIndices.Length));
                    mc.sharedMesh = null; mc.sharedMesh = col;
                }
                double newApply = sw.Elapsed.TotalMilliseconds / 50;
                Object.Destroy(go); Object.Destroy(m); Object.Destroy(col);

                Debug.Log($"[Equivalence] terrain chunk: {verts.Count} verts, render tris {indices.Count / 3}, collision tris " +
                          $"{ctx.CollisionIndices.Length / 3}. Mesh: managed {managed:F3} ms, burst {burst:F3} ms. " +
                          $"Upload+cook on main thread: old {oldApply:F3} ms, new (sync cook) {newApply:F3} ms");

                // Split: uploads alone, then cooking alone on fresh copies (so nothing is cached)
                const int k = 100;
                var up = new Mesh();
                sw.Restart();
                for (int i = 0; i < k; i++)
                {
                    up.Clear();
                    up.SetVertices(verts); up.SetColors(colors); up.SetUVs(0, uvs); up.SetTriangles(indices, 0); up.RecalculateNormals();
                }
                double oldUpload = sw.Elapsed.TotalMilliseconds / k;
                sw.Restart();
                for (int i = 0; i < k; i++)
                {
                    up.SetVertexBufferParams(ctx.Vertices.Length, ChunkVertex.Layout);
                    up.SetVertexBufferData(ctx.Vertices.AsArray(), 0, 0, ctx.Vertices.Length, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    up.SetIndexBufferParams(ctx.Indices16.Length, IndexFormat.UInt16);
                    up.SetIndexBufferData(ctx.Indices16.AsArray(), 0, 0, ctx.Indices16.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    up.subMeshCount = 1;
                    up.SetSubMesh(0, new SubMeshDescriptor(0, ctx.Indices16.Length), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                }
                double newUpload = sw.Elapsed.TotalMilliseconds / k;
                Object.Destroy(up);

                var oldCopies = new Mesh[k];
                var newCopies = new Mesh[k];
                var colVerts = ToVector3(ctx.CollisionVertices);
                var colIdx = ToInts(ctx.CollisionIndices);
                for (int i = 0; i < k; i++)
                {
                    oldCopies[i] = new Mesh();
                    oldCopies[i].SetVertices(verts); oldCopies[i].SetTriangles(indices, 0);
                    newCopies[i] = new Mesh();
                    newCopies[i].SetVertices(colVerts); newCopies[i].SetTriangles(colIdx, 0);
                }
                var opts = (MeshColliderCookingOptions)30;
                sw.Restart();
                for (int i = 0; i < k; i++) Physics.BakeMesh(oldCopies[i].GetInstanceID(), false, opts);
                double oldCook = sw.Elapsed.TotalMilliseconds / k;
                sw.Restart();
                for (int i = 0; i < k; i++) Physics.BakeMesh(newCopies[i].GetInstanceID(), false, opts);
                double newCook = sw.Elapsed.TotalMilliseconds / k;
                for (int i = 0; i < k; i++) { Object.Destroy(oldCopies[i]); Object.Destroy(newCopies[i]); }
                Debug.Log($"[Equivalence] upload: old {oldUpload:F3} ms, new {newUpload:F3} ms. Cook: render mesh {oldCook:F3} ms, " +
                          $"merged {newCook:F3} ms");
                yield return null;
            }
            finally
            {
                ctx.Dispose();
                tables.Dispose();
            }
        }

        static float Rand(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        static List<Vector3> ToVector3(NativeList<float3> l)
        {
            var r = new List<Vector3>(l.Length);
            for (int i = 0; i < l.Length; i++) r.Add(l[i]);
            return r;
        }

        static int[] ToInts(NativeList<int> l)
        {
            var r = new int[l.Length];
            for (int i = 0; i < l.Length; i++) r[i] = l[i];
            return r;
        }

        CulledMeshJob Job(MeshJobContext ctx, CustomBlockTables tables, float shade, bool collision, bool floorSolid = false)
        {
            return new CulledMeshJob
            {
                ChunkSize = CS,
                VoxelSize = 1f,
                SelfShadeIntensity = shade,
                BuildCollision = collision,
                MergeCollision = true,
                FloorSolid = floorSolid,
                Connectivity = ctx.Connectivity,
                OpenScratch = ctx.OpenScratch,
                FillStack = ctx.FillStack,
                Neighbours = ctx.Neighbours,
                Blocks = tables,
                Padded = ctx.Padded,
                FaceMasks = ctx.FaceMasks,
                Vertices = ctx.Vertices,
                Indices = ctx.Indices,
                Indices16 = ctx.Indices16,
                CollisionVertices = ctx.CollisionVertices,
                CollisionIndices = ctx.CollisionIndices,
                Bounds = ctx.Bounds,
            };
        }
    }
}
