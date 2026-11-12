using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    /// Hands back saved edits for chunk (0,0,0) from a worker thread a little later, as BanterPersister does.
    /// </summary>
    public class ThreadedPersister : MonoBehaviour, I_VoxelDataPersister
    {
        public byte[] Saved;
        public int Loads;
        public bool IsReady => true;
        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data) => true;

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            if ((x, y, z) != (0, 0, 0) || Saved == null)
                return false;
            Loads++;
            Task.Run(async () =>
            {
                await Task.Delay(20);
                vol.GetChunk((0, 0, 0))?.LoadChanges(Saved);
            });
            return true;
        }
    }

    public class PersistedEditsTests
    {
        [UnityTest]
        public IEnumerator EditsLoadedOffTheMainThreadAreAppliedAndMeshed()
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>();
            _cleanup.Add(camGo);
            camGo.transform.position = new Vector3(0, 0, 0);

            var go = new GameObject("Volume");
            _cleanup.Add(go);
            go.SetActive(false);
            var volume = go.AddComponent<Volume>();
#if UNITY_EDITOR
            volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab");
#endif
            volume.ChunkSize = 16;
            volume.IsInfinite = true;
            volume.InfiniteChunkRadius = 3;
            volume.InfiniteChunkBounds = new Vector3Int(0, 1, 0);
            volume.MeshingMode = MeshingMode.Culled;
            volume.CollisionMode = CollisionMode.MeshColliderConcave;
            volume.SelfShadingIntensity = 0.5f;
            var gen = go.AddComponent<TerrainGenerator>();
            gen.HeightMin = 0;
            gen.HeightMax = 10;
            gen.Trees = false;

            // A pillar of glass-ish blocks high in chunk (0,0,0), above the terrain (heights 0..10)
            var saved = new MemoryStream();
            using (var bw = new BinaryWriter(saved))
                for (int y = 12; y < 16; y++)
                {
                    bw.Write(4); bw.Write(y); bw.Write(4);
                    bw.Write(new Voxel { State = 1, Value = 2, Color = new Color32(10, 20, 30, 255) }.ToBytes());
                }
            var persister = go.AddComponent<ThreadedPersister>();
            persister.Saved = saved.ToArray();
            go.SetActive(true);

            Chunk centre = null;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 15f)
            {
                yield return null;
                centre = volume.GetChunk((0, 0, 0));
                if (centre != null && persister.Loads > 0)
                    break;
            }
            Assert.IsNotNull(centre, "chunk (0,0,0) created");
            Assert.Greater(persister.Loads, 0, "persister asked to load chunk (0,0,0)");
            // Let the worker hand the edits over and the chunk re-mesh
            start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 1.5f)
                yield return null;

            for (int y = 12; y < 16; y++)
            {
                Voxel v = centre.Voxels[4 + 16 * (y + 16 * 4)];
                Assert.AreEqual(1, v.State, $"edit at y {y} applied");
                Assert.AreEqual(2, v.Value, $"edit at y {y} value");
            }

            // The mesh shows them: compare with the managed mesher on the current voxels
            var nbs = new Chunk[27];
            for (int z = 0; z < 3; z++)
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                nbs[x + 3 * (y + 3 * z)] = volume.GetChunk((x - 1, y - 1, z - 1));
            var verts = new List<Vector3>(); var uvs = new List<Vector4>(); var colors = new List<Color32>(); var idx = new List<int>();
            Voxel[] voxels = centre.Voxels;
            MeshGenerator.GenerateCulled(verts, uvs, colors, idx, ref voxels, centre, nbs, volume.CustomBlocksDict, 1f, 0f,
                0, 0, 0, 16, 16, 16, 15, 15, 15, volume.SelfShadingIntensity);
            Assert.AreEqual(verts.Count, centre.GetComponent<MeshFilter>().sharedMesh.vertexCount, "mesh up to date with the loaded edits");
        }

        readonly List<GameObject> _cleanup = new();

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
