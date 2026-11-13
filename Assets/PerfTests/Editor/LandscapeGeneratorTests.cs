using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using PicaVoxel;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PicaVoxelPerf
{
    /// <summary>LandscapeGenerator: what land it makes, that it makes the same land on any thread, and its cost.</summary>
    public class LandscapeGeneratorTests
    {
        const int Y0 = -40, Y1 = 40;   // a volume of chunk rows -2..2 (as Blockbuster's)
        readonly List<Object> _made = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        T Make<T>() where T : Component
        {
            var go = new GameObject(typeof(T).Name);
            _made.Add(go);
            return go.AddComponent<T>();
        }

        [Test]
        public void TheLandHasShape()
        {
            var gen = Make<LandscapeGenerator>();
            gen.Seed = 1337;
            const int size = 240;
            var heights = new List<int>();
            long under = 0, cave = 0, deepCave = 0;
            int trees = 0, floating = 0, maxTop = int.MinValue, wet = 0;
            var surface = new Dictionary<byte, int>();
            var v = new Voxel();
            for (int z = -size / 2; z < size / 2; z++)
            for (int x = -size / 2; x < size / 2; x++)
            {
                int ground = int.MinValue, trunkBase = int.MinValue, trunkTop = int.MinValue;
                byte top = 255;
                bool water = false;
                for (int y = Y1 - 1; y >= Y0; y--)
                {
                    v = default;
                    gen.GenerateVoxel(x, y, z, ref v);
                    if (v.State != 0 && v.Value == gen.TreeTrunkValue)
                    {
                        if (trunkTop == int.MinValue) trunkTop = y;
                        trunkBase = y;
                        continue;
                    }
                    if (v.State != 0 && v.Value == gen.WaterValue)
                    {
                        water = true;
                        continue;
                    }
                    if (v.State != 0 && ground == int.MinValue)
                    {
                        ground = y;
                        top = v.Value;
                    }
                }
                if (trunkTop != int.MinValue)
                {
                    trees++;
                    maxTop = Mathf.Max(maxTop, trunkTop);
                    // The block under the trunk is solid ground
                    v = default; gen.GenerateVoxel(x, trunkBase - 1, z, ref v);
                    if (v.State == 0) floating++;
                }
                heights.Add(ground);
                if (water) wet++;
                surface[top] = surface.TryGetValue(top, out int n) ? n + 1 : 1;

                // Underground: from the ground down to the bedrock, how much is open
                int g = ground;
                for (int y = gen.BedrockHeight + 3; y < g; y++)
                {
                    under++;
                    v = default;
                    gen.GenerateVoxel(x, y, z, ref v);
                    if (v.State == 0)
                    {
                        cave++;
                        if (g - y >= 8) deepCave++;
                    }
                }
            }
            heights.Sort();
            int P(float q) => heights[Mathf.Clamp((int)(q * heights.Count), 0, heights.Count - 1)];
            float mountains = heights.Count(h => h >= 16) / (float)heights.Count;
            string surf = string.Join(", ", surface.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value * 100f / heights.Count:F0}%"));
            Debug.Log($"[Landscape] ground heights: min {heights[0]}, 10% {P(0.1f)}, median {P(0.5f)}, 90% {P(0.9f)}, 99% {P(0.99f)}, max {heights[^1]}; " +
                      $"mountain (16+) {mountains:P0}; under water {wet * 100f / heights.Count:F0}%; surface blocks {surf}");
            Debug.Log($"[Landscape] underground open: {cave * 100.0 / under:F1}% ({deepCave * 100.0 / under:F1}% 8+ deep); " +
                      $"trees {trees} ({trees * 10000f / (size * size):F1} per 100x100), highest trunk top {maxTop}, floating {floating}");

            Assert.Greater(heights[^1] - heights[0], 25, "a real range of heights");
            Assert.Greater(mountains, 0.03f, "some mountains");
            Assert.Less(mountains, 0.6f, "not all mountains");
            Assert.Greater(cave, 0, "caves");
            Assert.Greater(trees, 50, "trees");
            Assert.Greater(wet, 0, "some lakes");
            Assert.Less(wet / (float)heights.Count, 0.35f, "not mostly lakes");
            Assert.AreEqual(0, floating, "trees standing on ground");
            Assert.LessOrEqual(maxTop + 1, Y1 - 1, "trees (and their leaves) fit under the top of the volume");
        }

        [Test]
        public void SameLandOnEveryThread()
        {
            var gen = Make<LandscapeGenerator>();
            gen.Seed = 77;
            var chunks = new List<Vector3Int>();
            for (int cz = -3; cz < 3; cz++) for (int cy = -2; cy <= 2; cy++) for (int cx = -3; cx < 3; cx++) chunks.Add(new Vector3Int(cx, cy, cz));
            Voxel[][] Run(bool parallel)
            {
                var result = new Voxel[chunks.Count][];
                void One(int i)
                {
                    var c = chunks[i];
                    var vox = new Voxel[16 * 16 * 16];
                    for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++)
                        gen.GenerateVoxel(c.x * 16 + x, c.y * 16 + y, c.z * 16 + z, ref vox[x + 16 * (y + 16 * z)]);
                    result[i] = vox;
                }
                if (parallel) Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, One);
                else for (int i = 0; i < chunks.Count; i++) One(i);
                return result;
            }
            var a = Run(false);
            var b = Run(true);
            int differ = 0;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < a[i].Length; j++)
                    if (a[i][j].State != b[i][j].State || a[i][j].Value != b[i][j].Value) differ++;
            Assert.AreEqual(0, differ, "voxels that differ between one thread and many");
        }

        [Test]
        public void CostPerChunk()
        {
            var land = Make<LandscapeGenerator>(); land.Seed = 5;
            var old = Make<TerrainGenerator>(); old.Seed = 5; old.HeightMin = 0; old.HeightMax = 10;
            double Ms(I_VoxelDataGenerator g)
            {
                var vox = new Voxel[16 * 16 * 16];
                var sw = new Stopwatch();
                int n = 0;
                for (int round = 0; round < 2; round++)
                {
                    sw.Restart(); n = 0;
                    for (int cz = 10; cz < 14; cz++) for (int cy = -2; cy <= 2; cy++) for (int cx = 10; cx < 14; cx++)
                    {
                        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++)
                            g.GenerateVoxel(cx * 16 + x + round * 1024, cy * 16 + y, cz * 16 + z, ref vox[x + 16 * (y + 16 * z)]);
                        n++;
                    }
                }
                return sw.Elapsed.TotalMilliseconds / n;
            }
            double landMs = Ms(land), oldMs = Ms(old);
            // Where the time goes: the columns alone (one voxel a column, high in the air), then everything but caves
            var sw2 = new Stopwatch(); var vv = new Voxel(); int nc = 0;
            sw2.Start();
            for (int cz = 30; cz < 34; cz++) for (int cx = 30; cx < 34; cx++, nc++)
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) land.GenerateVoxel(cx * 16 + x, 39, cz * 16 + z, ref vv);
            double colMs = sw2.Elapsed.TotalMilliseconds / nc;
            land.Caves = false;
            double noCaveMs = Ms(land);
            land.Caves = true;
            Debug.Log($"[Landscape] breakdown: columns {colMs:F3} ms a 16x16, all but caves {noCaveMs:F3} ms a chunk");
            Debug.Log($"[Landscape] cost per 16^3 chunk on one thread: LandscapeGenerator {landMs:F3} ms, TerrainGenerator {oldMs:F3} ms");
            Assert.Less(landMs, 2.0, "under 2 ms a chunk on this PC");
        }
    }
}
