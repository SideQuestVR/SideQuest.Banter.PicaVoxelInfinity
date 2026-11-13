using System.Collections;
using System.Collections.Generic;
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
    /// Walking far off and respawning: afterwards every chunk position in range exists, holds the terrain the
    /// generator makes for it, and shows a mesh made from those voxels. (A chunk near spawn was seen missing on
    /// Quest after a respawn from far away.)
    /// </summary>
    public class TeleportRefillTests
    {
        readonly List<GameObject> _cleanup = new();
        static readonly Vector3 Spawn = new Vector3(3f, 2f, 3f);

        [UnityTest]
        public IEnumerator WalkFarThenRespawn()
        {
            var cam = MakeCamera();
            var volume = MakeVolume();
            yield return Wait(4f);
            AssertWhole(volume, cam.position, "after the first load");

            for (int round = 0; round < 3; round++)
            {
                // Walk away at 25 m/s, well past the radius, so chunks are recycled while their jobs run
                float start = Time.realtimeSinceStartup;
                Vector3 dir = Quaternion.Euler(0f, 40f + 110f * round, 0f) * Vector3.forward;
                while (Time.realtimeSinceStartup - start < 5f)
                {
                    cam.position += dir * 25f * Time.deltaTime;
                    yield return null;
                }
                // Respawn
                cam.position = Spawn;
                yield return Wait(3f + round);   // (different settle times, so the update slices fall differently)
                AssertWhole(volume, cam.position, $"after respawn {round + 1}");
            }
        }

        [UnityTest]
        public IEnumerator JumpAwayAndBackQuickly()
        {
            var cam = MakeCamera();
            var volume = MakeVolume();
            yield return Wait(4f);
            for (int round = 0; round < 4; round++)
            {
                cam.position = Spawn + new Vector3(16f * 20 * (round % 2 == 0 ? 1 : -1), 0f, 16f * 7 * round);
                yield return Wait(0.2f + 0.3f * round);   // back before (or just as) the far side has loaded
                cam.position = Spawn;
                yield return Wait(3f);
                AssertWhole(volume, cam.position, $"after quick jump {round + 1}");
            }
        }

        Transform MakeCamera()
        {
            // Earlier tests can leave a scene loaded (the sample scene's camera and volumes); the volume here
            // must follow this test's camera, so switch the others off
            foreach (var other in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) other.gameObject.SetActive(false);
            foreach (var other in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) other.gameObject.SetActive(false);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>();
            _cleanup.Add(camGo);
            camGo.transform.position = Spawn;
            return camGo.transform;
        }

        Volume MakeVolume()
        {
            var go = new GameObject("Volume");
            _cleanup.Add(go);
            go.SetActive(false);
            var volume = go.AddComponent<Volume>();
#if UNITY_EDITOR
            volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab");
#endif
            volume.ChunkSize = 16;
            volume.IsInfinite = true;
            volume.InfiniteChunkRadius = 5;
            volume.InfiniteChunkBounds = new Vector3Int(0, 1, 0);
            volume.MeshingMode = MeshingMode.Culled;
            volume.CaveCulling = false;   // this checks what is drawn, chunk by chunk,
            volume.CullWorldFloor = false; // against the managed mesher, which makes every face
            volume.CollisionMode = CollisionMode.MeshColliderConcave;
            volume.ChunkLayer = 0;
            var gen = go.AddComponent<TerrainGenerator>();
            gen.HeightMin = -6;
            gen.HeightMax = 6;
            gen.Trees = false;
            go.SetActive(true);
            return volume;
        }

        static IEnumerator Wait(float seconds)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds)
                yield return null;
        }

        // Every position in range has a chunk; and every chunk well inside the range (its neighbours all there)
        // holds the generator's terrain and shows the mesh for it
        static void AssertWhole(Volume v, Vector3 camPos, string when)
        {
            float unit = v.ChunkSize * v.VoxelSize;
            Vector3 c = (camPos - (v.transform.position - 0.5f * unit * Vector3.one)) / unit;
            var cc = new Vector3Int(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y), Mathf.RoundToInt(c.z));
            int r = v.InfiniteChunkRadius, cs = v.ChunkSize;
            var problems = new List<string>();
            var verts = new List<Vector3>(); var uvs = new List<Vector4>(); var colors = new List<Color32>(); var idx = new List<int>();
            var nbs = new Chunk[27];
            int checkedChunks = 0;
            for (int z = cc.z - r; z <= cc.z + r; z++)
            for (int y = cc.y - r; y <= cc.y + r; y++)
            for (int x = cc.x - r; x <= cc.x + r; x++)
            {
                if (y < -v.InfiniteChunkBounds.y || y > v.InfiniteChunkBounds.y) continue;
                var p = new Vector3Int(x, y, z);
                if ((cc - p).sqrMagnitude > r * r) continue;
                if (!v.Chunks.TryGetValue((x, y, z), out Chunk chunk) || chunk == null)
                {
                    problems.Add($"({x},{y},{z}) no chunk");
                    continue;
                }
                if ((cc - p).sqrMagnitude > (r - 2) * (r - 2)) continue;   // the outer rings may still be meshing
                checkedChunks++;

                // Its voxels are this position's terrain
                int wrong = 0;
                var fresh = new Voxel();
                for (int vz = 0; vz < cs; vz++)
                for (int vy = 0; vy < cs; vy++)
                for (int vx = 0; vx < cs; vx++)
                {
                    fresh = default;
                    v.GenerateVoxel(vx + cs * x, vy + cs * y, vz + cs * z, ref fresh);
                    if (fresh.State != chunk.Voxels[vx + cs * (vy + cs * vz)].State) wrong++;
                }
                if (wrong > 0) { problems.Add($"({x},{y},{z}) {wrong} voxels not this position's terrain"); continue; }

                // ...and its mesh shows them
                for (int nz = 0; nz < 3; nz++)
                for (int ny = 0; ny < 3; ny++)
                for (int nx = 0; nx < 3; nx++)
                    nbs[nx + 3 * (ny + 3 * nz)] = v.GetChunk((x + nx - 1, y + ny - 1, z + nz - 1));
                Voxel[] vox = chunk.Voxels;
                MeshGenerator.GenerateCulled(verts, uvs, colors, idx, ref vox, chunk, nbs, v.CustomBlocksDict, v.VoxelSize, 0f,
                    0, 0, 0, cs, cs, cs, cs - 1, cs - 1, cs - 1, v.SelfShadingIntensity);
                var mf = chunk.GetComponent<MeshFilter>();
                var mr = chunk.GetComponent<MeshRenderer>();
                var mc = chunk.GetComponent<MeshCollider>();
                int shown = mr.enabled && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0;
                if (shown != verts.Count)
                    problems.Add($"({x},{y},{z}) shows {shown} vertices, expects {verts.Count} (renderer {(mr.enabled ? "on" : "off")}, active {chunk.gameObject.activeInHierarchy})");
                else if (verts.Count > 0 && (!mc.enabled || mc.sharedMesh == null))
                    problems.Add($"({x},{y},{z}) has no collider [{State(chunk)} mc.enabled={mc.enabled} mc.mesh={(mc.sharedMesh != null ? mc.sharedMesh.name + "/" + mc.sharedMesh.vertexCount : "null")}]");
            }
            Debug.Log($"[Refill] {when}: centre {cc}, checked {checkedChunks}, problems {problems.Count}");
            Assert.IsEmpty(problems, $"{when}: " + string.Join("; ", problems));
        }

        // The chunk's private collider bookkeeping, for a failure message
        static string State(Chunk c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var name in new[] { "_generation", "_baking", "_bakeIndex", "_bakeGeneration", "_colActive", "_colliderOn",
                         "_rendererOn", "_hasData", "_dataReady", "_isMeshDirty", "_disableMrNextFrame", "updateColliderNextFrame" })
            {
                var f = typeof(Chunk).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                sb.Append(name).Append('=').Append(f != null ? f.GetValue(c) : "?").Append(' ');
            }
            var meshes = (Mesh[])typeof(Chunk).GetField("_colMeshes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(c);
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh m = meshes[i];
                sb.Append($"col{i}=").Append(ReferenceEquals(m, null) ? "none" : m == null ? "DESTROYED" : $"{m.GetInstanceID()}/{m.vertexCount}v").Append(' ');
            }
            var mc = c.GetComponent<MeshCollider>();
            Mesh shared = mc.sharedMesh;
            sb.Append("mc.mesh=").Append(ReferenceEquals(shared, null) ? "unset" : shared == null ? "DESTROYED" : shared.GetInstanceID().ToString())
              .Append(" mcId=").Append(mc.GetInstanceID()).Append(' ');
            return sb.ToString();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var g in _cleanup)
                if (g != null) Object.Destroy(g);
            _cleanup.Clear();
            yield return null;
        }
    }
}
