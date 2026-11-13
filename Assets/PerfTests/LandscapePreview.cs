using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PicaVoxel;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxelPerf
{
    /// <summary>
    /// Pictures of LandscapeGenerator's land with the landscape tile array material: an overview, the highest peak, a
    /// lake and a cave. Only runs with PV_SHOTS set (the folder the pictures go to); otherwise it is skipped.
    /// </summary>
    public class LandscapePreview
    {
        readonly List<Object> _made = new();

        [UnityTest]
        public IEnumerator Pictures()
        {
            string dir = System.Environment.GetEnvironmentVariable("PV_SHOTS");
            if (string.IsNullOrEmpty(dir))
            {
                Assert.Ignore("Set PV_SHOTS to a folder to take the pictures");
                yield break;
            }
#if UNITY_EDITOR
            Directory.CreateDirectory(dir);
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) c.gameObject.SetActive(false);
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) v.gameObject.SetActive(false);

            var camGo = Keep(new GameObject("Main Camera") { tag = "MainCamera" });
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var sunGo = Keep(new GameObject("Sun"));
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;
            sun.intensity = 1.1f;
            sunGo.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.skybox = Keep(new Material(Shader.Find("Skybox/Procedural")));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);
            DynamicGI.UpdateEnvironment();

            var gen = BuildVolume(out Volume volume);

            // Find the sights: the highest ground, a lake, and a tunnel, within 200 blocks of the origin
            var probe = Keep(new GameObject("Probe")).AddComponent<LandscapeGenerator>();
            probe.Seed = volume.GenerationSeed;
            Vector3Int peak = default, lake = default, cave = default;
            int best = int.MinValue;
            bool haveLake = false, haveCave = false;
            var vx = new Voxel();
            for (int z = -200; z <= 200; z += 3)
            for (int x = -200; x <= 200; x += 3)
            {
                int g = Ground(probe, x, z, out bool water);
                if (g > best) { best = g; peak = new Vector3Int(x, g, z); }
                if (water && !haveLake && Mathf.Abs(x) + Mathf.Abs(z) > 30) { lake = new Vector3Int(x, g, z); haveLake = true; }
                if (!haveCave && g > probe.SeaLevel + 3)
                    for (int y = g - 6; y > g - 20; y--)
                    {
                        vx = default; probe.GenerateVoxel(x, y, z, ref vx);
                        if (vx.State != 0) continue;
                        // Room to stand: open above and solid below
                        var up = new Voxel(); probe.GenerateVoxel(x, y + 1, z, ref up);
                        var down = new Voxel(); probe.GenerateVoxel(x, y - 1, z, ref down);
                        if (up.State == 0 && down.State != 0) { cave = new Vector3Int(x, y, z); haveCave = true; break; }
                    }
            }
            Debug.Log($"[Preview] peak {peak}, lake {(haveLake ? lake.ToString() : "none")}, cave {(haveCave ? cave.ToString() : "none")}");

            // Overview: high up, looking over the peak's country
            Vector3 toPeak = new Vector3(peak.x, 0, peak.z).normalized;
            if (toPeak == Vector3.zero) toPeak = Vector3.forward;
            yield return Shot(cam, volume, new Vector3(peak.x, 0, peak.z) - toPeak * 90f + Vector3.up * 44f, Quaternion.LookRotation(new Vector3(peak.x, peak.y, peak.z) - (new Vector3(peak.x, 0, peak.z) - toPeak * 90f + Vector3.up * 44f)), toPeak, "1_overview");

            // The peak from the ground nearby
            Vector3 foot = new Vector3(peak.x, 0, peak.z) - toPeak * 60f;
            foot.y = Ground(probe, Mathf.RoundToInt(foot.x), Mathf.RoundToInt(foot.z), out _) + 2.6f;
            yield return Shot(cam, volume, foot, Quaternion.LookRotation(new Vector3(peak.x, peak.y + 2, peak.z) - foot), toPeak, "2_mountain");

            if (haveLake)
            {
                // From the shore, a little above the water
                Vector3 shore = new Vector3(lake.x, probe.SeaLevel + 3.5f, lake.z) - new Vector3(1, 0, 1).normalized * 25f;
                yield return Shot(cam, volume, shore, Quaternion.LookRotation(new Vector3(lake.x, probe.SeaLevel, lake.z) - shore), Vector3.forward, "3_lake");
            }
            if (haveCave)
            {
                Vector3 inCave = new Vector3(cave.x + 0.5f, cave.y + 1.6f, cave.z + 0.5f);
                // Look down the tunnel: whichever of 8 directions stays open furthest at eye level
                float bestYaw = 0f; int bestRun = -1;
                for (int a = 0; a < 8; a++)
                {
                    Vector3 d = Quaternion.Euler(0f, a * 45f, 0f) * Vector3.forward;
                    int run = 0;
                    for (; run < 40; run++)
                    {
                        Vector3 p = inCave + d * (run + 1);
                        var t = new Voxel();
                        probe.GenerateVoxel(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z), ref t);
                        if (t.State != 0) break;
                    }
                    if (run > bestRun) { bestRun = run; bestYaw = a * 45f; }
                }
                yield return Shot(cam, volume, inCave, Quaternion.Euler(8f, bestYaw, 0f), Vector3.forward, "4_cave");
            }
#else
            yield break;
#endif
        }

#if UNITY_EDITOR
        LandscapeGenerator BuildVolume(out Volume volume)
        {
            var go = Keep(new GameObject("Landscape"));
            go.SetActive(false);
            volume = go.AddComponent<Volume>();
            volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab");
            volume.Material = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.garethiw.picavoxelinfinity/Materials/URP/PicaVoxel URP Landscape Terrain.mat");
            volume.ChunkSize = 16;
            volume.IsInfinite = true;
            volume.InfiniteChunkRadius = 12;
            volume.InfiniteChunkBounds = new Vector3Int(0, 2, 0);
            volume.GenerationSeed = 1337;
            volume.MeshingMode = MeshingMode.Culled;
            volume.MeshColliderMeshingMode = MeshingMode.Culled;
            volume.CollisionMode = CollisionMode.MeshColliderConcave;
            volume.SelfShadingIntensity = 0.7f;
            volume.CustomBlocks = new[]
            {
                new CustomBlock { VoxelValue = 12, HasTransparency = true },
                new CustomBlock { VoxelValue = 16, HasTransparency = true, HideFacesAgainstSame = true },
            };
            var gen = go.AddComponent<LandscapeGenerator>();
            var trees = go.AddComponent<TreeProcessor>();
            trees.TreeTrunkValue = 3;
            trees.TreeLeavesValue = 12;
            trees.Schedule = ProcessingSchedule.AfterGeneration;
            go.SetActive(true);
            return gen;
        }

        static int Ground(LandscapeGenerator g, int x, int z, out bool water)
        {
            water = false;
            var v = new Voxel();
            for (int y = 39; y > -40; y--)
            {
                v = default;
                g.GenerateVoxel(x, y, z, ref v);
                if (v.State == 0 || v.Value == g.TreeTrunkValue) continue;
                if (v.Value == g.WaterValue) { water = true; continue; }
                return y;
            }
            return -40;
        }

        IEnumerator Shot(Camera cam, Volume volume, Vector3 pos, Quaternion rot, Vector3 _, string name)
        {
            // Positions here are the generator's; a volume at the origin puts each voxel half a chunk back from them
            cam.transform.SetPositionAndRotation(pos - Vector3.one * (volume.ChunkSize * 0.5f * volume.VoxelSize), rot);
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 8f)
                yield return null;
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(System.Environment.GetEnvironmentVariable("PV_SHOTS"), name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
            Debug.Log($"[Preview] {name} at {pos} chunks {volume.Chunks.Count}");
        }
#endif

        T Keep<T>(T o) where T : Object { _made.Add(o); return o; }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var o in _made) if (o != null) Object.Destroy(o);
            _made.Clear();
            yield return null;
        }
    }
}
