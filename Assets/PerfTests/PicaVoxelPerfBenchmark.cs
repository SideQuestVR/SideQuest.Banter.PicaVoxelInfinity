using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PicaVoxel;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxelPerf
{
    /// <summary>
    /// Persists nothing, so the benchmark measures generation and meshing alone.
    /// </summary>
    public class NullPersister : MonoBehaviour, I_VoxelDataPersister
    {
        public bool IsReady => true;
        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data) => true;
        public bool LoadChunk(Volume vol, int x, int y, int z) => false;
    }

    /// <summary>
    /// A Blockbuster-shaped infinite terrain (Culled meshing, concave colliders, radius 10, y bounded to +-2 chunks,
    /// trees, self-shading), timed through load, standing still, flying and border edits. Uses only the public API
    /// so the same test runs against the library before and after a change. Results are appended to the file in
    /// PV_RESULTS (or PerfResults.txt in the project), labelled with PV_LABEL.
    /// </summary>
    public class PicaVoxelPerfBenchmark
    {
        const string ChunkPrefabPath = "Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab";

        readonly StringBuilder _report = new StringBuilder();
        Volume _volume;
        Camera _camera;

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BlockbusterTerrain()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            var label = Environment.GetEnvironmentVariable("PV_LABEL") ?? "unlabelled";
            _report.AppendLine($"=== {label}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  cores={SystemInfo.processorCount}");

            Setup();
            StatsReset();

            // Load: until the number of chunks with a live collider stops changing for 3 s.
            var load = new List<double>();
            var sw = Stopwatch.StartNew();
            int lastCount = -1;
            double stableSince = 0;
            int gc0 = GC.CollectionCount(0);
            double prev = Time.realtimeSinceStartupAsDouble;
            while (sw.Elapsed.TotalSeconds < 120)
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                load.Add((now - prev) * 1000.0);
                prev = now;

                int count = CountCollidedChunks();
                if (count != lastCount)
                {
                    lastCount = count;
                    stableSince = sw.Elapsed.TotalSeconds;
                }
                else if (count > 0 && sw.Elapsed.TotalSeconds - stableSince > 3.0)
                    break;
            }
            double loadSeconds = stableSince;
            Report("load", load, GC.CollectionCount(0) - gc0);
            _report.AppendLine($"               work: {StatsSummary()}");
            CountTrees(out long leaves, out long trunks);
            _report.AppendLine($"  trees after load: leaves={leaves} trunks={trunks}");
            _report.AppendLine($"  load settled after {loadSeconds:F2}s, chunks={_volume.Chunks.Count}, collided={lastCount}, " +
                               $"verts={TotalVertices()}, colliderTris={TotalColliderTriangles()}");

            if (Environment.GetEnvironmentVariable("PV_QUICK") == "1")
            {
                Flush();
                yield break;
            }

            // Idle: camera still for 10 s.
            yield return Measure("idle", 10f, null);

            // Fly: 10 m/s along +x for 15 s.
            Vector3 start = _camera.transform.position;
            yield return Measure("fly", 15f, t => _camera.transform.position = start + new Vector3(10f * t, 0f, 0f));

            // Settle after flying, then edits at chunk borders.
            yield return Measure("settle", 5f, null);
            CheckStale("after settle");
            yield return Edits();
            yield return Measure("after-edits", 2f, null);
            CheckStale("after edits");

            Flush();
            UnityEngine.Object.Destroy(_volume.gameObject);
            UnityEngine.Object.Destroy(_camera.gameObject);
            yield return null;
        }

        void Setup()
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            _camera = camGo.AddComponent<Camera>();
            camGo.transform.position = new Vector3(0f, 20f, 0f);
            camGo.transform.rotation = Quaternion.Euler(30f, 0f, 0f);

            var go = new GameObject("Volume");
            go.SetActive(false);
            _volume = go.AddComponent<Volume>();
#if UNITY_EDITOR
            _volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ChunkPrefabPath);
#endif
            Assert.IsNotNull(_volume.ChunkPrefab, "chunk prefab");
            _volume.ChunkSize = 16;
            _volume.VoxelSize = 1f;
            _volume.IsInfinite = true;
            _volume.InfiniteChunkRadius = 10;
            _volume.InfiniteChunkBounds = new Vector3Int(0, 2, 0);
            _volume.InfiniteUpdateInterval = 0.25f;
            _volume.InfiniteUpdateSlicing = 8;
            _volume.InfiniteChunkRadiusUpdateMargin = 3;
            _volume.GenerationSeed = 1337;
            _volume.MeshingMode = MeshingMode.Culled;
            _volume.MeshColliderMeshingMode = MeshingMode.Culled;
            _volume.CollisionMode = CollisionMode.MeshColliderConcave;
            _volume.SelfShadingIntensity = 0.7f;
            _volume.Material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            _volume.ChunkLayer = 0;

            var cubeGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = cubeGo.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(cubeGo);
            _volume.CustomBlocks = new[]
            {
                new CustomBlock { VoxelValue = 5, AllowOrientation = true, Mesh = cube },
                new CustomBlock { VoxelValue = 7, HasTransparency = true },
                new CustomBlock { VoxelValue = 12, HasTransparency = true },
            };

            var gen = go.AddComponent<TerrainGenerator>();
            gen.HeightMax = 10;
            gen.HeightMin = 0;
            gen.BedrockHeight = -32;
            gen.TreeSeed = 1;
            gen.MinTreeHeight = 8;
            gen.MaxTreeHeight = 12;

            var trees = go.AddComponent<TreeProcessor>();
            trees.Schedule = ProcessingSchedule.AfterGeneration;
            trees.TreeLeavesValue = 12;

            go.AddComponent<NullPersister>();
            go.SetActive(true);
        }

        IEnumerator Measure(string phase, float seconds, Action<float> drive)
        {
            StatsReset();
            var frames = new List<double>();
            int gc0 = GC.CollectionCount(0);
            double begin = Time.realtimeSinceStartupAsDouble;
            double prev = begin;
            while (Time.realtimeSinceStartupAsDouble - begin < seconds)
            {
                drive?.Invoke((float)(Time.realtimeSinceStartupAsDouble - begin));
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                frames.Add((now - prev) * 1000.0);
                prev = now;
            }
            Report(phase, frames, GC.CollectionCount(0) - gc0);
            _report.AppendLine($"               work: {StatsSummary()}");
        }

        static readonly Type StatsType = Type.GetType("PicaVoxel.PicaVoxelStats, GarethIW.PicaVoxelInfinity");
        static void StatsReset() => StatsType?.GetMethod("Reset")?.Invoke(null, null);
        static string StatsSummary() => StatsType?.GetMethod("Summary")?.Invoke(null, null) as string ?? "(no stats)";

        IEnumerator Edits()
        {
            // Voxels on chunk borders and corners near the origin, toggled off and back on, as a player would at a
            // chunk edge. Each call is timed (it meshes the chunk immediately), plus the frames that follow.
            var calls = new List<double>();
            var frames = new List<double>();
            var rng = new System.Random(7);
            int landed = 0;
            StatsReset();
            int cs = _volume.ChunkSize;
            for (int i = 0; i < 40; i++)
            {
                int cx = rng.Next(-2, 3), cz = rng.Next(-2, 3);
                int lx = rng.Next(2) == 0 ? 0 : cs - 1;
                int lz = rng.Next(2) == 0 ? 0 : cs - 1;
                // Just under the surface band (heights 0..10), in chunk y 0.
                int ly = rng.Next(0, 8);
                // Chunk P's voxel v spans [P*cs - cs/2 + v, +1) in volume space; aim at its centre.
                var world = _volume.transform.TransformPoint(
                    new Vector3((cx * cs + lx) - cs * 0.5f + 0.5f, ly - cs * 0.5f + 0.5f, (cz * cs + lz) - cs * 0.5f + 0.5f) * _volume.VoxelSize);

                var voxel = new Voxel { State = (byte)(i % 2 == 0 ? 0 : 1), Value = (byte)(i % 4 == 3 ? 5 : 1), Color = Color.white };
                if (voxel.Value == 5) voxel.State = 2;

                var sw = Stopwatch.StartNew();
                if (_volume.SetVoxelAtWorldPosition(world, voxel, out Chunk _, out (int, int, int) _, false) != null)
                    landed++;
                calls.Add(sw.Elapsed.TotalMilliseconds);

                double prev = Time.realtimeSinceStartupAsDouble;
                for (int f = 0; f < 3; f++)
                {
                    yield return null;
                    double now = Time.realtimeSinceStartupAsDouble;
                    frames.Add((now - prev) * 1000.0);
                    prev = now;
                }
            }
            Report("edit-call", calls, 0);
            Report("edit-frames", frames, 0);
            _report.AppendLine($"               edits landed={landed}/40 work: {StatsSummary()}");
        }

        // Re-meshes every chunk with the managed Culled mesher from its current voxels and compares with what it shows:
        // a chunk whose mesh doesn't match its data (or its neighbours') was left stale by the scheduling.
        void CheckStale(string when)
        {
            int cs = _volume.ChunkSize, checkedChunks = 0, countStale = 0, shadeStale = 0;
            var verts = new List<Vector3>(); var uvs = new List<Vector4>(); var colors = new List<Color32>(); var idx = new List<int>();
            var nbs = new Chunk[27];
            var examples = new List<string>();
            foreach (var kv in _volume.Chunks)
            {
                Chunk c = kv.Value;
                var p = kv.Key;
                // Only chunks whose whole neighbourhood is loaded: the outer ring legitimately waits on chunks to come
                bool full = true;
                for (int z = 0; z < 3; z++)
                for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    var np = (p.Item1 + x - 1, p.Item2 + y - 1, p.Item3 + z - 1);
                    nbs[x + 3 * (y + 3 * z)] = _volume.GetChunk(np);
                    bool outOfBounds = np.Item2 < -_volume.InfiniteChunkBounds.y || np.Item2 > _volume.InfiniteChunkBounds.y;
                    if (nbs[x + 3 * (y + 3 * z)] == null && !outOfBounds) full = false;
                }
                if (!full) continue;
                checkedChunks++;

                Voxel[] v = c.Voxels;
                MeshGenerator.GenerateCulled(verts, uvs, colors, idx, ref v, c, nbs, _volume.CustomBlocksDict, _volume.VoxelSize, 0f,
                    0, 0, 0, cs, cs, cs, cs - 1, cs - 1, cs - 1, _volume.SelfShadingIntensity);
                var mf = c.GetComponent<MeshFilter>();
                var mr = c.GetComponent<MeshRenderer>();
                int shown = mr.enabled && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0;
                if (shown != verts.Count)
                {
                    countStale++;
                    if (examples.Count < 5) examples.Add($"{p} shows {shown} expects {verts.Count}");
                    continue;
                }
                if (shown == 0) continue;
                var shownColors = new List<Color32>();
                mf.sharedMesh.GetColors(shownColors);
                for (int i = 0; i < shownColors.Count; i++)
                    if (Mathf.Abs(shownColors[i].r - colors[i].r) > 1 || Mathf.Abs(shownColors[i].g - colors[i].g) > 1)
                    {
                        shadeStale++;
                        break;
                    }
            }
            _report.AppendLine($"  stale {when}: checked={checkedChunks} wrongFaceCount={countStale} wrongShading={shadeStale} " +
                               string.Join("; ", examples));
        }

        void CountTrees(out long leaves, out long trunks)
        {
            leaves = 0; trunks = 0;
            foreach (var c in _volume.Chunks.Values)
                foreach (var v in c.Voxels)
                {
                    if (v.Value == 12 && v.State == 2) leaves++;
                    else if (v.Value == 3 && v.State == 1) trunks++;
                }
        }

        int CountCollidedChunks()
        {
            int n = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                if (c == null) continue;
                var mc = c.GetComponent<MeshCollider>();
                if (mc != null && mc.enabled && mc.sharedMesh != null) n++;
            }
            return n;
        }

        long TotalVertices()
        {
            long n = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                var mf = c != null ? c.GetComponent<MeshFilter>() : null;
                var mr = c != null ? c.GetComponent<MeshRenderer>() : null;
                if (mf != null && mr != null && mr.enabled && mf.sharedMesh != null) n += mf.sharedMesh.vertexCount;
            }
            return n;
        }

        long TotalColliderTriangles()
        {
            long n = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                var mc = c != null ? c.GetComponent<MeshCollider>() : null;
                if (mc != null && mc.enabled && mc.sharedMesh != null)
                    for (int s = 0; s < mc.sharedMesh.subMeshCount; s++)
                        n += (long)mc.sharedMesh.GetIndexCount(s) / 3;
            }
            return n;
        }

        void Report(string phase, List<double> ms, int gcs)
        {
            if (ms.Count == 0)
            {
                _report.AppendLine($"  {phase,-12} (no samples)");
                return;
            }
            var sorted = ms.OrderBy(v => v).ToArray();
            double P(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
            _report.AppendLine(
                $"  {phase,-12} n={ms.Count,5} mean={ms.Average(),7:F2} p50={P(0.5),7:F2} p95={P(0.95),7:F2} " +
                $"p99={P(0.99),7:F2} max={sorted[sorted.Length - 1],8:F2} >16.7={ms.Count(v => v > 16.7),4} " +
                $">33={ms.Count(v => v > 33.3),4} sum={ms.Sum() / 1000.0,7:F2}s gc0={gcs}");
        }

        void Flush()
        {
            var path = Environment.GetEnvironmentVariable("PV_RESULTS");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Application.dataPath, "..", "PerfResults.txt");
            File.AppendAllText(path, _report.ToString());
            Debug.Log(_report.ToString());
        }
    }
}
