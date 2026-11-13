using System.Collections.Generic;
using NUnit.Framework;
using PicaVoxel;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace PicaVoxelPerf
{
    /// <summary>
    /// ChunkVisibilityJob: which chunks the walk keeps, on small grids built by hand (one rule each), and on a random
    /// cave world checked against rays: every chunk a ray from the camera reaches first must have been kept.
    /// </summary>
    public class CaveCullingWalkTests
    {
        // Faces: 0 -x, 1 +x, 2 -y, 3 +y, 4 -z, 5 +z
        const int NX = 0, PX = 1, NY = 2, PY = 3, NZ = 4, PZ = 5;

        /// <summary>A world of chunk masks, filled as the test says, and the walk over it.</summary>
        sealed class World : System.IDisposable
        {
            public readonly int3 Bound, Size;
            public readonly int Radius;
            public NativeArray<ushort> Masks;
            readonly NativeArray<byte> _visible, _incoming;
            readonly NativeArray<int3> _queue;

            public World(int3 bound, int radius, ushort fill)
            {
                Bound = bound;
                Radius = radius;
                Size = new int3(bound.x > 0 ? 2 * bound.x + 1 : 2 * radius + 1,
                                bound.y > 0 ? 2 * bound.y + 1 : 2 * radius + 1,
                                bound.z > 0 ? 2 * bound.z + 1 : 2 * radius + 1);
                int cells = Size.x * Size.y * Size.z;
                Masks = new NativeArray<ushort>(cells, Allocator.Persistent);
                for (int i = 0; i < cells; i++) Masks[i] = fill;
                _visible = new NativeArray<byte>(cells, Allocator.Persistent);
                _incoming = new NativeArray<byte>(cells, Allocator.Persistent);
                _queue = new NativeArray<int3>(cells, Allocator.Persistent);
            }

            public int Cell(int x, int y, int z) => ChunkConnectivity.CellIndex(new int3(x, y, z), Bound, Size);
            public void Set(int x, int y, int z, int mask) => Masks[Cell(x, y, z)] = (ushort)mask;

            public void Walk(int3 start)
            {
                new ChunkVisibilityJob
                {
                    Masks = Masks, Visible = _visible, Incoming = _incoming, Queue = _queue,
                    Size = Size, Bound = Bound, Start = start, Radius = Radius,
                }.Run();
            }

            public bool Visible(int x, int y, int z) => _visible[Cell(x, y, z)] != 0;

            public int VisibleCount()
            {
                int n = 0;
                foreach (byte b in _visible) if (b != 0) n++;
                return n;
            }

            public void Dispose()
            {
                Masks.Dispose(); _visible.Dispose(); _incoming.Dispose(); _queue.Dispose();
            }
        }

        static int Joins(params (int a, int b)[] pairs)
        {
            int m = 0;
            foreach (var (a, b) in pairs) m |= 1 << ChunkConnectivity.PairBit(a, b);
            return m;
        }

        // Y bounded to -2..2 as Blockbuster's is, x and z unbounded
        static readonly int3 BlockbusterBound = new int3(0, 2, 0);

        [Test]
        public void PairNumberingIsTheFifteenPairsInOrder()
        {
            int expect = 0;
            for (int a = 0; a < 5; a++)
            for (int b = a + 1; b < 6; b++, expect++)
            {
                Assert.AreEqual(expect, ChunkConnectivity.PairBit(a, b));
                Assert.AreEqual(expect, ChunkConnectivity.PairBit(b, a));
            }
            Assert.AreEqual(15, expect);
            Assert.AreEqual(ChunkConnectivity.All, (1 << 15) - 1);
        }

        [Test]
        public void OpenWorldIsVisibleOutToTheRadius()
        {
            using var w = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            w.Walk(int3.zero);
            int expect = 0;
            for (int z = -8; z <= 8; z++) for (int y = -2; y <= 2; y++) for (int x = -8; x <= 8; x++)
            {
                bool inRange = x * x + y * y + z * z <= 36;
                if (inRange) expect++;
                if (x >= -6 && x <= 6 && z >= -6 && z <= 6)
                    Assert.AreEqual(inRange, w.Visible(x, y, z), $"({x},{y},{z})");
            }
            Assert.AreEqual(expect, w.VisibleCount());
        }

        [Test]
        public void SolidGroundHidesAllButItsTopLayer()
        {
            using var w = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            for (int z = -8; z <= 8; z++) for (int x = -8; x <= 8; x++) { w.Set(x, -1, z, 0); w.Set(x, -2, z, 0); }
            w.Walk(int3.zero);
            Assert.IsTrue(w.Visible(3, 0, 2), "open air layer");
            Assert.IsTrue(w.Visible(3, -1, 2), "the ground's top layer is entered from above");
            Assert.IsFalse(w.Visible(3, -2, 2), "sealed under it");
            Assert.IsFalse(w.Visible(0, -2, 0), "directly under the camera too");
        }

        [Test]
        public void AShaftLetsTheWalkDown()
        {
            using var w = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            for (int z = -8; z <= 8; z++) for (int x = -8; x <= 8; x++) { w.Set(x, -1, z, 0); w.Set(x, -2, z, 0); }
            // A vertical shaft at (2, *, 0): down through both layers
            w.Set(2, -1, 0, Joins((NY, PY)));
            w.Set(2, -2, 0, Joins((NY, PY)));
            w.Walk(int3.zero);
            Assert.IsTrue(w.Visible(2, -1, 0));
            Assert.IsTrue(w.Visible(2, -2, 0), "the bottom of the shaft");
            Assert.IsFalse(w.Visible(3, -2, 0), "the rock beside the shaft is not");
            Assert.IsFalse(w.Visible(2, -2, 1));
        }

        [Test]
        public void ABendFollowsTheConnection()
        {
            using var w = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            for (int z = -8; z <= 8; z++) for (int x = -8; x <= 8; x++) { w.Set(x, -1, z, 0); w.Set(x, -2, z, 0); }
            // Down into (2,-1,0), out of its +x side, into (3,-1,0), and down out of its bottom
            w.Set(2, -1, 0, Joins((PY, PX)));
            w.Set(3, -1, 0, Joins((NX, NY)));
            w.Walk(int3.zero);
            Assert.IsTrue(w.Visible(3, -2, 0), "round the bend");
            Assert.IsFalse(w.Visible(2, -2, 0), "straight on is rock: this chunk's top doesn't join its bottom");

            // The wrong way round: the second chunk's -x doesn't join its bottom, so the walk can't get through
            w.Set(3, -1, 0, Joins((NX, PZ)));
            w.Walk(int3.zero);
            Assert.IsFalse(w.Visible(3, -2, 0));
        }

        [Test]
        public void ACameraInsideRockSeesOnlyItsNeighbours()
        {
            using var w = new World(BlockbusterBound, 6, 0);
            w.Walk(new int3(0, 0, 0));
            Assert.AreEqual(7, w.VisibleCount(), "its own chunk and the six round it");
            Assert.IsTrue(w.Visible(0, 0, 0));
            Assert.IsTrue(w.Visible(1, 0, 0));
            Assert.IsFalse(w.Visible(2, 0, 0));
        }

        [Test]
        public void APathNeverTurnsBackOnItself()
        {
            using var w = new World(BlockbusterBound, 6, 0);
            // (1,0,0) joins -x to +y, (1,1,0) joins -y to +y, (1,2,0) joins -y to -x: a U that comes back to x = 0
            w.Set(1, 0, 0, Joins((NX, PY)));
            w.Set(1, 1, 0, Joins((NY, PY)));
            w.Set(1, 2, 0, Joins((NY, NX)));
            w.Set(0, 2, 0, ChunkConnectivity.All);
            w.Walk(int3.zero);
            Assert.IsTrue(w.Visible(1, 2, 0), "up the U");
            Assert.IsFalse(w.Visible(0, 2, 0), "back along it towards the camera's column");
        }

        [Test]
        public void ChunksWithoutAMaskAreSeenThrough()
        {
            // Unknown chunks hold All: the walk goes through them, to a sealed chunk behind
            using var w = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            w.Set(4, 0, 0, 0);
            w.Walk(int3.zero);
            Assert.IsTrue(w.Visible(4, 0, 0), "entered");
            Assert.IsTrue(w.Visible(5, 0, 1), "and round the side of it");
            // Straight behind the sealed chunk is hidden (a path round it would have to turn back on itself)
            Assert.IsFalse(w.Visible(5, 0, 0), "directly behind the rock");
        }

        [Test]
        public void CoordinatesWrapOnUnboundedAxes()
        {
            using var near = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            using var far = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            near.Walk(int3.zero);
            far.Walk(new int3(-137, 0, 211));
            Assert.AreEqual(near.VisibleCount(), far.VisibleCount());
            for (int z = -6; z <= 6; z++) for (int y = -2; y <= 2; y++) for (int x = -6; x <= 6; x++)
                Assert.AreEqual(near.Visible(x, y, z), far.Visible(-137 + x, y, 211 + z), $"({x},{y},{z})");
        }

        [Test]
        public void ACameraOutsideTheBoundsStartsInTheNearestRow()
        {
            using var above = new World(BlockbusterBound, 6, ChunkConnectivity.All);
            above.Walk(new int3(0, 9, 0));
            Assert.IsTrue(above.Visible(0, 2, 0), "the top row");
            Assert.IsTrue(above.Visible(0, -2, 0), "open all the way down");

            using var below = new World(BlockbusterBound, 6, 0);
            below.Walk(new int3(0, -9, 0));
            Assert.IsTrue(below.Visible(0, -2, 0));
            Assert.IsTrue(below.Visible(0, -1, 0));
            Assert.AreEqual(6, below.VisibleCount(), "the start and the five neighbours inside the bounds");
        }

        // A random cave world, and rays from cameras in it ///////////////////////////////////////////////////////

        const int CS = 16;

        static float Hash01(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 362437);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        // Smooth 3D value noise, 0..1
        static float Noise(float x, float y, float z, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y), iz = Mathf.FloorToInt(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            float L(float a, float b, float t) => a + (b - a) * t;
            float c00 = L(Hash01(ix, iy, iz, seed), Hash01(ix + 1, iy, iz, seed), fx);
            float c10 = L(Hash01(ix, iy + 1, iz, seed), Hash01(ix + 1, iy + 1, iz, seed), fx);
            float c01 = L(Hash01(ix, iy, iz + 1, seed), Hash01(ix + 1, iy, iz + 1, seed), fx);
            float c11 = L(Hash01(ix, iy + 1, iz + 1, seed), Hash01(ix + 1, iy + 1, iz + 1, seed), fx);
            return L(L(c00, c10, fy), L(c01, c11, fy), fz);
        }

        // Voxel (x, y, z) of the world, where chunk c covers voxels c*16 .. c*16+15 on each axis
        static bool IsSolid(int x, int y, int z, int seed)
        {
            float ground = 6f + 10f * Noise(x / 24f, 0f, z / 24f, seed + 1);
            if (y > ground) return false;
            if (y < -30) return true;
            float a = Noise(x / 9f, y / 7f, z / 9f, seed + 2) - 0.5f;
            float b = Noise(x / 9f, y / 7f, z / 9f, seed + 3) - 0.5f;
            if (a * a + b * b < 0.0035f) return false;                        // winding tunnels
            if (Noise(x / 11f, y / 8f, z / 11f, seed + 4) > 0.86f) return false;  // caverns
            return true;
        }

        sealed class VoxelWorld
        {
            public readonly int3 Bound = new int3(2, 2, 2);
            public readonly bool[] Solid;
            public readonly int Wx, Wy, Wz, X0, Y0, Z0;

            public VoxelWorld(int seed)
            {
                Wx = (2 * Bound.x + 1) * CS; Wy = (2 * Bound.y + 1) * CS; Wz = (2 * Bound.z + 1) * CS;
                X0 = -Bound.x * CS; Y0 = -Bound.y * CS; Z0 = -Bound.z * CS;
                Solid = new bool[Wx * Wy * Wz];
                for (int z = 0; z < Wz; z++) for (int y = 0; y < Wy; y++) for (int x = 0; x < Wx; x++)
                    Solid[x + Wx * (y + Wy * z)] = IsSolid(X0 + x, Y0 + y, Z0 + z, seed);
            }

            public bool InWorld(int x, int y, int z) =>
                x >= X0 && y >= Y0 && z >= Z0 && x < X0 + Wx && y < Y0 + Wy && z < Z0 + Wz;

            public bool IsSolidAt(int x, int y, int z) => Solid[(x - X0) + Wx * ((y - Y0) + Wy * (z - Z0))];

            public static int ChunkOf(int v) => Mathf.FloorToInt(v / (float)CS);

            // Each chunk's mask by a plain flood fill over its own voxels
            public ushort Mask(int cx, int cy, int cz)
            {
                int bx = cx * CS, by = cy * CS, bz = cz * CS;
                var seen = new bool[CS * CS * CS];
                int mask = 0;
                var stack = new Stack<int>();
                for (int s = 0; s < seen.Length; s++)
                {
                    int sx = s % CS, sy = s / CS % CS, sz = s / (CS * CS);
                    if (seen[s] || IsSolidAt(bx + sx, by + sy, bz + sz)) continue;
                    int touched = 0;
                    seen[s] = true;
                    stack.Push(s);
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        int x = i % CS, y = i / CS % CS, z = i / (CS * CS);
                        if (x == 0) touched |= 1; if (x == CS - 1) touched |= 2;
                        if (y == 0) touched |= 4; if (y == CS - 1) touched |= 8;
                        if (z == 0) touched |= 16; if (z == CS - 1) touched |= 32;
                        void Try(int nx, int ny, int nz)
                        {
                            if (nx < 0 || ny < 0 || nz < 0 || nx >= CS || ny >= CS || nz >= CS) return;
                            int n = nx + CS * (ny + CS * nz);
                            if (seen[n] || IsSolidAt(bx + nx, by + ny, bz + nz)) return;
                            seen[n] = true; stack.Push(n);
                        }
                        Try(x - 1, y, z); Try(x + 1, y, z); Try(x, y - 1, z); Try(x, y + 1, z); Try(x, y, z - 1); Try(x, y, z + 1);
                    }
                    mask |= ChunkConnectivity.PairsOf(touched);
                }
                return (ushort)mask;
            }
        }

        // Steps a ray voxel by voxel (one axis at a time) from the camera until it hits a solid voxel or leaves the
        // world, adding every chunk it passes through, and the one it hits, to `seen`
        static void March(VoxelWorld w, Vector3 from, Vector3 dir, HashSet<int3> seen)
        {
            int x = Mathf.FloorToInt(from.x), y = Mathf.FloorToInt(from.y), z = Mathf.FloorToInt(from.z);
            Vector3 d = dir.normalized;
            int sx = d.x > 0 ? 1 : -1, sy = d.y > 0 ? 1 : -1, sz = d.z > 0 ? 1 : -1;
            float tdx = d.x == 0 ? float.MaxValue : Mathf.Abs(1f / d.x);
            float tdy = d.y == 0 ? float.MaxValue : Mathf.Abs(1f / d.y);
            float tdz = d.z == 0 ? float.MaxValue : Mathf.Abs(1f / d.z);
            float tx = d.x == 0 ? float.MaxValue : (d.x > 0 ? (x + 1 - from.x) : (from.x - x)) * tdx;
            float ty = d.y == 0 ? float.MaxValue : (d.y > 0 ? (y + 1 - from.y) : (from.y - y)) * tdy;
            float tz = d.z == 0 ? float.MaxValue : (d.z > 0 ? (z + 1 - from.z) : (from.z - z)) * tdz;
            for (int step = 0; step < 400; step++)
            {
                if (!w.InWorld(x, y, z)) return;
                seen.Add(new int3(VoxelWorld.ChunkOf(x), VoxelWorld.ChunkOf(y), VoxelWorld.ChunkOf(z)));
                if (w.IsSolidAt(x, y, z)) return;
                if (tx <= ty && tx <= tz) { x += sx; tx += tdx; }
                else if (ty <= tz) { y += sy; ty += tdy; }
                else { z += sz; tz += tdz; }
            }
        }

        [Test]
        public void EveryChunkARayReachesFirstIsKept()
        {
            int checkedChunks = 0, hiddenTotal = 0, cameras = 0;
            foreach (int seed in new[] { 3, 11 })
            {
                var world = new VoxelWorld(seed);
                var masks = new Dictionary<int3, ushort>();
                using var w = new World(world.Bound, 30, ChunkConnectivity.All);
                for (int cz = -2; cz <= 2; cz++) for (int cy = -2; cy <= 2; cy++) for (int cx = -2; cx <= 2; cx++)
                    w.Set(cx, cy, cz, world.Mask(cx, cy, cz));

                var rng = new System.Random(seed);
                var spots = new List<Vector3>();
                // Cameras: in the open above the ground, and in caves (open voxels with rock above them)
                for (int tries = 0; tries < 20000 && spots.Count < 8; tries++)
                {
                    int x = rng.Next(-28, 28), z = rng.Next(-28, 28), y = rng.Next(-30, 20);
                    if (!world.InWorld(x, y, z) || world.IsSolidAt(x, y, z)) continue;
                    bool covered = false;
                    for (int u = 1; u < 12 && !covered; u++)
                        if (world.InWorld(x, y + u, z) && world.IsSolidAt(x, y + u, z)) covered = true;
                    bool wantCave = spots.Count % 2 == 1;
                    if (covered == wantCave) spots.Add(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
                }
                Assert.GreaterOrEqual(spots.Count, 4, $"seed {seed}: cameras found");

                foreach (Vector3 cam in spots)
                {
                    cameras++;
                    var start = new int3(VoxelWorld.ChunkOf(Mathf.FloorToInt(cam.x)), VoxelWorld.ChunkOf(Mathf.FloorToInt(cam.y)), VoxelWorld.ChunkOf(Mathf.FloorToInt(cam.z)));
                    w.Walk(start);

                    var reached = new HashSet<int3>();
                    for (int r = 0; r < 1500; r++)
                    {
                        Vector3 dir = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
                        if (dir.sqrMagnitude < 0.01f) continue;
                        March(world, cam, dir, reached);
                    }
                    foreach (int3 c in reached)
                    {
                        checkedChunks++;
                        Assert.IsTrue(w.Visible(c.x, c.y, c.z), $"seed {seed}, camera {cam}: chunk {c} was reached by a ray but hidden");
                    }
                    hiddenTotal += 125 - w.VisibleCount();
                }
            }
            Debug.Log($"[CaveCulling] {cameras} cameras, {checkedChunks} ray-reached chunks all kept; on average {hiddenTotal / (float)cameras:F0} of 125 chunks hidden");
            // Not vacuous: a good share of the world is hidden from a typical camera
            Assert.Greater(hiddenTotal / (float)cameras, 125 * 0.1f, "chunks hidden on average");
        }
    }
}
