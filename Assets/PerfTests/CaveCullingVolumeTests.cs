using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PicaVoxel;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxelPerf
{
    /// <summary>
    /// Cave culling in a real volume (LandscapeGenerator's caves and mountains): it hides chunks, nothing a camera can
    /// see is hidden (physics rays from the camera hit only chunks that are still drawn), an edit that opens a way
    /// down shows the chunk below, and the floor faces are not made.
    /// </summary>
    public class CaveCullingVolumeTests
    {
        readonly List<Object> _made = new();
        Volume _volume;
        LandscapeGenerator _gen;
        Transform _cam;

        // The centre of voxel (gx, gy, gz) (volume-wide voxel coordinates, as the generator uses them) in the world
        static Vector3 ToWorld(int gx, int gy, int gz) => new Vector3(gx - 7.5f, gy - 7.5f, gz - 7.5f);

        IEnumerator Setup(ShadowCastingMode shadows = ShadowCastingMode.Off, int radius = 8, bool solid = false)
        {
            // Earlier tests can leave a scene loaded: this volume must follow this test's camera
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) c.gameObject.SetActive(false);
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) v.gameObject.SetActive(false);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>();
            _made.Add(camGo);
            _cam = camGo.transform;
            _cam.position = new Vector3(0, 40, 0);

            var go = new GameObject("Volume");
            _made.Add(go);
            go.SetActive(false);
            _volume = go.AddComponent<Volume>();
#if UNITY_EDITOR
            _volume.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.garethiw.picavoxelinfinity/Prefabs/PicaVoxelChunk.prefab");
#endif
            _volume.ChunkSize = 16;
            _volume.IsInfinite = true;
            _volume.InfiniteChunkRadius = radius;
            _volume.InfiniteChunkBounds = new Vector3Int(0, 2, 0);
            _volume.GenerationSeed = 1337;
            _volume.MeshingMode = MeshingMode.Culled;
            _volume.CollisionMode = CollisionMode.MeshColliderConcave;
            _volume.ChunkLayer = 0;
            _volume.CastShadows = shadows;
            go.AddComponent<NullPersister>();
            if (solid)
                go.AddComponent<InfiniteSolidGenerator>();   // rock all the way: nothing is open until it's dug
            else
            {
                _gen = go.AddComponent<LandscapeGenerator>();
                _gen.Trees = false;
            }
            go.SetActive(true);
            yield return null;
        }

        // Until the number of chunks with a live collider stops changing for 3 s
        IEnumerator WaitForLoad(float giveUp = 90f)
        {
            float start = Time.realtimeSinceStartup, stableSince = start;
            int last = -1;
            while (Time.realtimeSinceStartup - start < giveUp)
            {
                yield return null;
                int n = 0;
                foreach (var c in _volume.Chunks.Values)
                    if (c.GetComponent<MeshCollider>().enabled) n++;
                if (n != last) { last = n; stableSince = Time.realtimeSinceStartup; }
                else if (n > 0 && Time.realtimeSinceStartup - stableSince > 3f) yield break;
            }
        }

        static IEnumerator Wait(float seconds)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds) yield return null;
        }

        int GroundAt(int gx, int gz)
        {
            var v = new Voxel();
            for (int y = 39; y > -40; y--)
            {
                v = default;
                _gen.GenerateVoxel(gx, y, gz, ref v);
                if (v.State != 0 && v.Value != _gen.WaterValue) return y;
            }
            return -40;
        }

        long DrawnFaces()
        {
            long verts = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                var mr = c.GetComponent<MeshRenderer>();
                var mf = c.GetComponent<MeshFilter>();
                if (mr.enabled && mf.sharedMesh != null) verts += mf.sharedMesh.vertexCount;
            }
            return verts / 4;
        }

        // Rays in every direction from the camera, against the chunk colliders (which culling leaves on): every chunk
        // one hits first must still be drawn
        void AssertRaysOnlyHitDrawnChunks(string when, int rays = 4000)
        {
            var rng = new System.Random(7);
            int hits = 0, bad = 0;
            string example = null;
            for (int i = 0; i < rays; i++)
            {
                Vector3 d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
                if (d.sqrMagnitude < 0.01f) continue;
                if (!Physics.Raycast(_cam.position, d.normalized, out RaycastHit hit, 400f)) continue;
                var chunk = hit.collider.GetComponent<Chunk>();
                if (chunk == null) continue;
                hits++;
                if (chunk.CaveCulled)
                {
                    bad++;
                    example ??= $"chunk {chunk.Position} hit at {hit.point} from {_cam.position}";
                }
            }
            Debug.Log($"[CaveCullingVolume] {when}: {hits} rays hit chunks, {bad} of them culled; {_volume.CulledChunkCount} of {_volume.Chunks.Count} chunks culled");
            Assert.Greater(hits, rays / 4, $"{when}: rays that hit a chunk");
            Assert.AreEqual(0, bad, $"{when}: rays that hit a chunk the culling hid, e.g. {example}");
        }

        [UnityTest]
        public IEnumerator HidesSealedChunksAndNeverWhatIsSeen()
        {
            yield return Setup();
            // The world mapping used here, against what the volume says is at those places
            foreach (var (gx, gy, gz) in new[] { (3, 2, 5), (-20, -9, 31), (40, 17, -12) })
            {
                var v = _volume.GetVoxelAtWorldPosition(ToWorld(gx, gy, gz), out _, out _);
                // (before anything has generated there is nothing to read: only check what exists)
                if (v.HasValue) { var want = new Voxel(); _gen.GenerateVoxel(gx, gy, gz, ref want); Assert.AreEqual(want.State, v.Value.State, $"voxel at {gx},{gy},{gz}"); }
            }

            int groundY = GroundAt(0, 0);
            _cam.position = ToWorld(0, groundY + 3, 0);
            yield return WaitForLoad();

            int chunks = _volume.Chunks.Count;
            Assert.Greater(chunks, 200, "chunks loaded");
            Assert.Greater(_volume.CulledChunkCount, 0, "culled chunks");
            long drawnOn = DrawnFaces();
            AssertRaysOnlyHitDrawnChunks("on the surface");

            // No faces under the bottom row of chunks
            int floorFaces = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                if (c.Position.y != -2) continue;
                var mesh = c.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                var verts = new List<Vector3>(); var normals = new List<Vector3>();
                mesh.GetVertices(verts); mesh.GetNormals(normals);
                for (int i = 0; i < verts.Count; i++)
                    if (normals[i].y < -0.9f && Mathf.Abs(verts[i].y) < 0.001f) floorFaces++;
            }
            Assert.AreEqual(0, floorFaces, "vertices on the underside of the bottom row");

            // Switched off, everything with a mesh is drawn again
            _volume.CaveCulling = false;
            yield return Wait(1.5f);
            long drawnOff = DrawnFaces();
            Assert.AreEqual(0, _volume.CulledChunkCount, "culled with culling off");
            foreach (var c in _volume.Chunks.Values)
                Assert.IsFalse(c.CaveCulled, "a chunk still culled with culling off");
            Debug.Log($"[CaveCullingVolume] drawn faces on the surface: {drawnOn} with culling, {drawnOff} without ({100f * drawnOn / drawnOff:F0}%)");
            Assert.Less(drawnOn, drawnOff * 0.8f, "faces drawn: culling should save a good share");
            _volume.CaveCulling = true;

            // Into a cave: an open voxel with rock above and below, a little way down
            Vector3Int cave = default;
            bool found = false;
            for (int z = -60; z <= 60 && !found; z += 3)
            for (int x = -60; x <= 60 && !found; x += 3)
            {
                int g = GroundAt(x, z);
                if (g < 4) continue;
                for (int y = g - 6; y > Mathf.Max(g - 22, -22) && !found; y--)
                {
                    var here = new Voxel(); var up = new Voxel(); var up2 = new Voxel(); var below = new Voxel();
                    _gen.GenerateVoxel(x, y, z, ref here); _gen.GenerateVoxel(x, y + 1, z, ref up);
                    _gen.GenerateVoxel(x, y + 2, z, ref up2); _gen.GenerateVoxel(x, y - 1, z, ref below);
                    if (here.State == 0 && up.State == 0 && up2.State != 0 && below.State != 0) { cave = new Vector3Int(x, y, z); found = true; }
                }
            }
            Assert.IsTrue(found, "a cave to stand in");
            _cam.position = ToWorld(cave.x, cave.y, cave.z);
            yield return Wait(4f);
            yield return WaitForLoad();
            var inChunk = _volume.GetChunk((Mathf.FloorToInt(cave.x / 16f), Mathf.FloorToInt(cave.y / 16f), Mathf.FloorToInt(cave.z / 16f)));
            Assert.IsNotNull(inChunk, "the chunk the camera is in");
            Assert.IsFalse(inChunk.CaveCulled, "the camera's own chunk");
            AssertRaysOnlyHitDrawnChunks($"in a cave at {cave}");
            Debug.Log($"[CaveCullingVolume] in a cave: {DrawnFaces()} faces drawn");
        }

        [UnityTest]
        public IEnumerator DiggingAShaftShowsTheChunkBelow()
        {
            yield return Setup();
            int groundY = GroundAt(0, 0);
            _cam.position = ToWorld(0, groundY + 3, 0);
            yield return WaitForLoad();

            // A sealed chunk in the bottom row near the camera, with a column of ground above it that has no cave
            Chunk target = null;
            int tx = 0, tz = 0, ground = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                if (c.Position.y != -2 || !c.CaveCulled) continue;
                if (Mathf.Abs(c.Position.x) > 4 || Mathf.Abs(c.Position.z) > 4) continue;
                foreach (var (ox, oz) in new[] { (8, 8), (4, 4), (12, 4), (4, 12), (12, 12) })
                {
                    int gx = c.Position.x * 16 + ox, gz = c.Position.z * 16 + oz;
                    int g = GroundAt(gx, gz);
                    if (g < 0 || g > 14) continue;
                    // the column is rock all the way down to the chunk (no cave to give the dig a head start)
                    bool rock = true;
                    var v = new Voxel();
                    for (int y = g - 1; y >= -20 && rock; y--) { v = default; _gen.GenerateVoxel(gx, y, gz, ref v); rock = v.State != 0; }
                    if (!rock) continue;
                    target = c; tx = gx; tz = gz; ground = g;
                    break;
                }
                if (target != null) break;
            }
            Assert.IsNotNull(target, "a sealed chunk to dig down to");

            for (int y = ground; y >= -20; y--)
                _volume.SetVoxelAtWorldPosition(ToWorld(tx, y, tz), default, out _, out _);
            yield return Wait(2f);

            Debug.Log($"[CaveCullingVolume] dug a shaft from {ground} to -20 at ({tx},{tz}): chunk {target.Position} culled={target.CaveCulled}");
            Assert.IsFalse(target.CaveCulled, "the chunk at the bottom of the shaft is drawn once there is a way down to it");
            AssertRaysOnlyHitDrawnChunks("after digging");
        }

        // The chunk whose cell contains the world position (found from where the chunks really are)
        Chunk ChunkContaining(Vector3 p)
        {
            foreach (var c in _volume.Chunks.Values)
            {
                var o = c.transform.position;
                if (p.x >= o.x && p.x < o.x + 16f && p.y >= o.y && p.y < o.y + 16f && p.z >= o.z && p.z < o.z + 16f) return c;
            }
            return null;
        }

        // The "camera chunk" streaming and cave culling start from must be the chunk the camera is in, in every half of
        // every chunk (it named the next chunk along in the far half, which broke cave culling from inside a tunnel)
        [UnityTest]
        public IEnumerator CameraChunkIsTheChunkTheCameraIsIn()
        {
            yield return Setup(radius: 4, solid: true);
            yield return WaitForLoad();
            int checkedCount = 0;
            foreach (float x in new[] { -17f, -8.01f, -7.99f, -0.1f, 0f, 0.1f, 7.9f, 8.1f, 15.9f, 24.5f })
            foreach (float y in new[] { -9f, -0.5f, 0.5f, 7.5f })
            foreach (float z in new[] { -12f, -0.1f, 3f, 8.5f })
            {
                _cam.position = new Vector3(x, y, z);
                yield return null;
                yield return null;
                var inside = ChunkContaining(_cam.position);
                if (inside == null) continue;   // (not loaded)
                checkedCount++;
                Assert.AreEqual(new Vector3Int(inside.Position.x, inside.Position.y, inside.Position.z), _volume.CameraChunk,
                    $"camera at {_cam.position} is in chunk {inside.Position}");
            }
            Assert.Greater(checkedCount, 100, "positions checked");
        }

        // Digging along a tunnel through solid rock, as in the report: whichever half of the chunk the camera is in, the
        // chunks round it, ahead and behind along the tunnel, are drawn
        [UnityTest]
        public IEnumerator InATunnelThroughRockTheChunksAroundYouAreDrawn()
        {
            yield return Setup(radius: 4, solid: true);
            yield return WaitForLoad();
            // Two tunnels along x, crossing several chunks: one in the upper halves of y and z, one in the lower
            foreach (var (ty, tz) in new[] { (0.5f, 0.5f), (-3.5f, -3.5f) })
                for (float x = -23.5f; x < 40f; x += 1f)
                    for (float dy = 0f; dy < 4f; dy += 1f)   // (head height: the camera rises and falls in it with a jump or a crouch)
                        _volume.SetVoxelAtWorldPosition(new Vector3(x, ty + dy, tz), default, out _, out _);
            yield return Wait(2.5f);

            int positions = 0;
            foreach (var (ty, tz) in new[] { (0.5f, 0.5f), (-3.5f, -3.5f) })
            foreach (float x in new[] { -17.5f, -12.5f, -8.5f, -0.5f, 0.5f, 3.5f, 7.5f, 8.5f, 15.5f, 20.5f, 23.5f, 24.5f, 31.5f })
            foreach (float eye in new[] { 0.25f, 1.5f, 3.25f })   // crouched, standing, jumping
            {
                _cam.position = new Vector3(x, ty + eye, tz);
                yield return Wait(0.8f);
                var here = ChunkContaining(_cam.position);
                Assert.IsNotNull(here, $"chunk at {_cam.position}");
                positions++;
                Assert.IsFalse(here.CaveCulled, $"camera at {_cam.position}: its own chunk {here.Position}");
                // The six chunks next to it are always kept (the camera is in the tunnel's open space: look any way)
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    var next = _volume.GetChunk((here.Position.x + dx, here.Position.y + dy, here.Position.z + dz));
                    if (next != null)
                        Assert.IsFalse(next.CaveCulled, $"camera at {_cam.position} in chunk {here.Position}: neighbour {next.Position} is culled");
                }
                // ...and so are the chunks the tunnel runs through, two along each way (the tunnel joins their sides)
                for (int dx = -2; dx <= 2; dx++)
                {
                    var along = _volume.GetChunk((here.Position.x + dx, here.Position.y, here.Position.z));
                    if (along == null || along.Position.x < -1 || along.Position.x > 1) continue;   // (the tunnel runs through chunks -1 to 1 whole)
                    Assert.IsFalse(along.CaveCulled, $"camera at {_cam.position}: tunnel chunk {along.Position} is culled");
                }
                AssertRaysOnlyHitDrawnChunks($"in the tunnel at {_cam.position}", 1200);
            }
            Debug.Log($"[CaveCullingVolume] tunnel: {positions} camera positions, every chunk round them drawn; {_volume.CulledChunkCount} of {_volume.Chunks.Count} chunks culled");
        }

        [UnityTest]
        public IEnumerator ShadowCastingVolumesKeepCulledChunksCastingShadows()
        {
            yield return Setup(ShadowCastingMode.On);
            _cam.position = ToWorld(0, GroundAt(0, 0) + 3, 0);
            yield return WaitForLoad();

            int culledWithMesh = 0, visibleWithMesh = 0;
            foreach (var c in _volume.Chunks.Values)
            {
                var mr = c.GetComponent<MeshRenderer>();
                var mesh = c.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (c.CaveCulled)
                {
                    culledWithMesh++;
                    Assert.IsTrue(mr.enabled, "a culled chunk keeps casting shadows");
                    Assert.AreEqual(ShadowCastingMode.ShadowsOnly, mr.shadowCastingMode, "...as shadows only");
                }
                else
                {
                    visibleWithMesh++;
                    Assert.AreEqual(ShadowCastingMode.On, mr.shadowCastingMode, "a drawn chunk draws and casts");
                }
            }
            Debug.Log($"[CaveCullingVolume] shadows: {culledWithMesh} culled and {visibleWithMesh} drawn chunks with meshes");
            Assert.Greater(culledWithMesh, 0);
            Assert.Greater(visibleWithMesh, 0);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var o in _made) if (o != null) Object.Destroy(o);
            _made.Clear();
            yield return null;
        }
    }
}
