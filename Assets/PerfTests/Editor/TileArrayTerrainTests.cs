using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PicaVoxel.EditorTools;
using UnityEditor;
using UnityEngine;

namespace PicaVoxelPerf
{
    /// <summary>
    /// The tile array terrain shader and its tools, against the tile sheet shader: the same texels up close, much
    /// less shimmer at a distance, cut-out leaves that keep their coverage, and a sky fade that meets the skybox.
    /// </summary>
    public class TileArrayTerrainTests
    {
        const string SheetPath = "Packages/com.garethiw.picavoxelinfinity/Textures/minecrafttiles-pv-padded2.png";
        const int Cols = 14, Rows = 6, Padding = 1, Size = 512;

        static TileArrayBuilder.Settings Settings => new() { Columns = Cols, Rows = Rows, Padding = Padding, AlphaCutoff = 0.5f, PreserveCoverage = true };

        readonly List<Object> _made = new();
        Material _skyBefore;
        T Keep<T>(T o) where T : Object { _made.Add(o); return o; }

        [SetUp] public void SetUp() => _skyBefore = RenderSettings.skybox;

        [TearDown]
        public void TearDown()
        {
            RenderSettings.skybox = _skyBefore;
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        // ---- the array

        [Test]
        public void SlicesAreTheSheetsTilesExactly()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath);
            var array = Keep(TileArrayBuilder.Build(sheet, Settings));
            Assert.AreEqual(16, array.width);
            Assert.AreEqual(Cols * Rows, array.depth);
            Assert.AreEqual(5, array.mipmapCount, "16, 8, 4, 2, 1");

            Color32[] src = SheetPixels(out int w);
            int cell = w / Cols, wrong = 0;
            for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
            {
                Color32[] slice = array.GetPixels32(row * Cols + col, 0);
                for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    Color32 a = slice[y * 16 + x], b = src[(row * cell + Padding + y) * w + col * cell + Padding + x];
                    if (a.a != b.a || (b.a > 0 && (a.r != b.r || a.g != b.g || a.b != b.b))) wrong++;
                }
            }
            Assert.AreEqual(0, wrong, "texels that differ from the sheet");
        }

        [Test]
        public void CutOutTilesKeepTheirCoverageDownTheMips()
        {
            var array = Keep(TileArrayBuilder.Build(AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath), Settings));
            foreach (int slice in new[] { 6, 7, 12 })   // the leaf and other cut-out tiles on the bottom row
            {
                float full = Solid(array.GetPixels32(slice, 0));
                for (int mip = 1; mip <= 3; mip++)
                {
                    float c = Solid(array.GetPixels32(slice, mip));
                    Debug.Log($"[TileArray] slice {slice} mip {mip}: solid {c:P0} (full size {full:P0})");
                    Assert.AreEqual(full, c, 1f / Mathf.Pow(4, 3 - mip) + 0.07f, $"slice {slice} mip {mip} coverage");
                }
            }
        }

        // ---- rendering

        [Test]
        public void UpCloseItLooksTheSameAsTheTileSheetShader()
        {
            var scene = new TestScene(this);
            scene.Camera.transform.SetPositionAndRotation(new Vector3(0.3f, 3f, 0.2f), Quaternion.Euler(55f, 20f, 0f));
            Color32[] before = scene.Render(scene.SheetMaterial);
            Color32[] after = scene.Render(scene.ArrayMaterial(cubemap: null));

            // The lower half of the view is within a few metres: texels several pixels wide
            int differ = 0, n = 0;
            for (int y = 0; y < Size / 2; y++)
            for (int x = 0; x < Size; x++)
            {
                n++;
                if (Diff(before[y * Size + x], after[y * Size + x]) > 3) differ++;
            }
            Debug.Log($"[TileArray] close-up: {differ} of {n} pixels differ by more than 3/255");
            Assert.Less(differ, n / 200, "close-up pixels that changed");
        }

        [Test]
        public void DistantTerrainShimmersFarLess()
        {
            var scene = new TestScene(this);
            float before = scene.Shimmer(scene.SheetMaterial);
            float after = scene.Shimmer(scene.ArrayMaterial(cubemap: null));
            float pointMips = scene.Shimmer(scene.ArrayMaterial(cubemap: null, smooth: false));
            Debug.Log($"[TileArray] shimmer (mean per-pixel change between frames, 0-255): tile sheet {before:F2}, " +
                      $"tile array {after:F2}, tile array with nearest mips {pointMips:F2}");
            Assert.Less(after, before * 0.5f, "smooth mips shimmer at most half as much");
            Assert.Less(pointMips, before * 0.75f, "nearest mips shimmer less too");
        }

        // In every direction, the terrain shader's sky colour (from the built cubemap) is what the sky shows: an
        // inside-out sphere round the camera, all of it fully faded, against the sky with nothing in front of it
        [Test]
        public void SkyCubemapMatchesASixSidedSkybox()
        {
            var sky = Keep(SixSidedSky());
            var cube = Keep(SkyboxCubemapBuilder.Render(sky, 256));
            RenderSettings.skybox = sky;

            var scene = new TestScene(this, withTerrain: false);
            var shell = Keep(new GameObject("Sky Shell"));
            shell.AddComponent<MeshFilter>().sharedMesh = Keep(InsideOutSphere(50f));
            var faded = scene.ArrayMaterial(cube);
            faded.SetFloat("_DrawDistance", 0.01f);
            faded.SetFloat("_DistanceFalloff", 1f);
            var shellRenderer = shell.AddComponent<MeshRenderer>();
            shellRenderer.sharedMaterial = faded;

            float worst = 0f;
            foreach (var rot in new[] { new Vector3(0, 0, 0), new Vector3(0, 90, 0), new Vector3(0, 180, 0), new Vector3(0, 270, 0),
                         new Vector3(-80, 30, 0), new Vector3(80, -40, 0), new Vector3(-20, 135, 0), new Vector3(15, 300, 0) })
            {
                scene.Camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(rot));
                shell.SetActive(false);
                Color32[] a = scene.Render(null);
                shell.SetActive(true);
                Color32[] b = scene.Render(null);
                float mean = MeanDiff(a, b, 0, Size);
                worst = Mathf.Max(worst, mean);
                Debug.Log($"[TileArray] sky {rot}: mean difference {mean:F2}/255");
            }
            Assert.Less(worst, 1.5f, "the cubemap shows the same sky");
        }

        [Test]
        public void FadedTerrainMeetsTheSkyBehindIt()
        {
            var sky = Keep(SixSidedSky());
            var cube = Keep(SkyboxCubemapBuilder.Render(sky, 256));
            RenderSettings.skybox = sky;

            var scene = new TestScene(this);
            scene.Camera.transform.SetPositionAndRotation(new Vector3(0f, 2f, 0f), Quaternion.Euler(4f, 30f, 0f));
            var faded = scene.ArrayMaterial(cube);
            faded.SetFloat("_DrawDistance", 50f);
            faded.SetFloat("_DistanceFalloff", 4f);
            Color32[] withTerrain = scene.Render(faded);
            scene.Terrain.SetActive(false);
            Color32[] skyOnly = scene.Render(null);
            scene.Terrain.SetActive(true);

            // Rows looking at terrain more than about 60 m off, where it is fully faded: just under the horizon
            int horizon = Mathf.RoundToInt(Size * (0.5f + Mathf.Tan(4f * Mathf.Deg2Rad) / (2f * Mathf.Tan(30f * Mathf.Deg2Rad))));
            int below = Mathf.RoundToInt(Size * Mathf.Tan(Mathf.Atan(2f / 70f)) / (2f * Mathf.Tan(30f * Mathf.Deg2Rad)));
            float mean = MeanDiff(withTerrain, skyOnly, horizon - below, horizon - 2);
            float nearMean = MeanDiff(withTerrain, skyOnly, 0, Size / 4);
            Debug.Log($"[TileArray] fade: rows {horizon - below}..{horizon - 2} differ from the sky by {mean:F2}/255 (near terrain: {nearMean:F1})");
            Assert.Greater(nearMean, 10f, "near terrain is drawn (the comparison means something)");
            Assert.Less(mean, 1.5f, "fully faded terrain is the sky's colour");
        }

        // ---- helpers

        class TestScene
        {
            public readonly Camera Camera;
            public readonly GameObject Terrain;
            public readonly Material SheetMaterial;
            readonly TileArrayTerrainTests _t;
            readonly RenderTexture _rt;
            readonly Texture2D _read;
            readonly MeshRenderer _renderer;
            Texture2DArray _array;

            public TestScene(TileArrayTerrainTests t, bool withTerrain = true)
            {
                _t = t;
                var camGo = t.Keep(new GameObject("Test Camera"));
                Camera = camGo.AddComponent<Camera>();
                Camera.enabled = false;
                Camera.fieldOfView = 60f;
                Camera.nearClipPlane = 0.05f;
                Camera.farClipPlane = 1000f;
                Camera.clearFlags = CameraClearFlags.Skybox;
                Camera.allowMSAA = false;
                Camera.allowHDR = false;
                _rt = t.Keep(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 });
                _read = t.Keep(new Texture2D(Size, Size, TextureFormat.RGBA32, false));

                var lightGo = t.Keep(new GameObject("Test Sun"));
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                Terrain = t.Keep(new GameObject("Test Terrain"));
                Terrain.AddComponent<MeshFilter>().sharedMesh = t.Keep(FaceGrid(300));
                _renderer = Terrain.AddComponent<MeshRenderer>();
                Terrain.SetActive(withTerrain);

                SheetMaterial = t.Keep(new Material(Shader.Find("PicaVoxel/PicaVoxel URP Infinite Terrain")));
                SheetMaterial.SetTexture("_TileSheet", AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath));
                SheetMaterial.SetVector("_NumTiles", new Vector4(Cols, Rows));
                SheetMaterial.SetFloat("_TilePadding", Padding);
                SheetMaterial.SetColor("_Tint", Color.white);
                SheetMaterial.SetFloat("_DrawDistance", 180f);
                SheetMaterial.SetFloat("_DistanceFalloff", 20f);
            }

            public Material ArrayMaterial(Cubemap cubemap, bool smooth = true)
            {
                _array ??= _t.Keep(TileArrayBuilder.Build(AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath), Settings));
                var m = _t.Keep(new Material(Shader.Find("PicaVoxel/PicaVoxel URP Infinite Terrain Tile Array")));
                m.SetTexture("_TileArray", _array);
                m.SetTexture("_TileSheet", AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath));
                m.SetVector("_NumTiles", new Vector4(Cols, Rows));
                m.SetColor("_Tint", Color.white);
                m.SetFloat("_DrawDistance", 180f);
                m.SetFloat("_DistanceFalloff", 20f);
                m.SetFloat("_SmoothMips", smooth ? 1f : 0f);
                if (smooth) m.EnableKeyword("_SMOOTH_MIPS"); else m.DisableKeyword("_SMOOTH_MIPS");
                foreach (var k in new[] { "_SKYFADE_CUBEMAP", "_SKYFADE_REFLECTION", "_SKYFADE_COLOR" }) m.DisableKeyword(k);
                if (cubemap != null)
                {
                    m.SetTexture("_SkyCube", cubemap);
                    m.SetFloat("_SkyFade", 0f);
                    m.EnableKeyword("_SKYFADE_CUBEMAP");
                }
                else
                {
                    m.SetFloat("_SkyFade", 2f);
                    m.EnableKeyword("_SKYFADE_COLOR");
                }
                return m;
            }

            public Color32[] Render(Material terrain)
            {
                if (terrain != null) _renderer.sharedMaterial = terrain;
                Camera.targetTexture = _rt;
                Camera.Render();
                Camera.targetTexture = null;
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                RenderTexture.active = prev;
                return _read.GetPixels32();
            }

            // Mean change of each pixel between frames as the camera creeps sideways (a third of a texel a frame),
            // over the terrain from about 10 m to 120 m off
            public float Shimmer(Material terrain)
            {
                const float pitch = 8f;
                int horizon = Mathf.RoundToInt(Size * (0.5f + Mathf.Tan(pitch * Mathf.Deg2Rad) / (2f * Mathf.Tan(30f * Mathf.Deg2Rad))));
                float pxPerRad = Size / (2f * Mathf.Tan(30f * Mathf.Deg2Rad));
                int y0 = horizon - Mathf.RoundToInt(pxPerRad * Mathf.Tan(Mathf.Atan(2f / 10f))) ;
                int y1 = horizon - Mathf.RoundToInt(pxPerRad * Mathf.Tan(Mathf.Atan(2f / 120f)));
                y0 = Mathf.Max(0, y0);
                Color32[] prev = null;
                float total = 0f;
                int frames = 0;
                for (int i = 0; i < 9; i++)
                {
                    Camera.transform.SetPositionAndRotation(new Vector3(0.37f + i * 0.021f, 2f, 0.11f), Quaternion.Euler(pitch, 12f, 0f));
                    Color32[] px = Render(terrain);
                    if (prev != null) { total += MeanDiff(px, prev, y0, y1); frames++; }
                    prev = px;
                }
                return total / frames;
            }
        }

        // A flat field of block tops (UVs as the Culled mesher writes them), tiles picked at random but repeatably
        static Mesh FaceGrid(int n)
        {
            var rng = new System.Random(42);
            var verts = new List<Vector3>(n * n * 4);
            var normals = new List<Vector3>(n * n * 4);
            var uvs = new List<Vector4>(n * n * 4);
            var colors = new List<Color32>(n * n * 4);
            var idx = new List<int>(n * n * 6);
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int col, row;
                do { col = rng.Next(Cols); row = rng.Next(Rows); } while (col == 13);   // (column 13 is mostly clear)
                float px = x - n / 2, pz = z - n / 2;
                int b = verts.Count;
                verts.Add(new Vector3(px, 0, pz)); verts.Add(new Vector3(px, 0, pz + 1)); verts.Add(new Vector3(px + 1, 0, pz + 1)); verts.Add(new Vector3(px + 1, 0, pz));
                uvs.Add(new Vector4(0, 0, col, row)); uvs.Add(new Vector4(0, 1, col, row)); uvs.Add(new Vector4(1, 1, col, row)); uvs.Add(new Vector4(1, 0, col, row));
                for (int k = 0; k < 4; k++) { normals.Add(Vector3.up); colors.Add(new Color32(255, 255, 255, 255)); }
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2); idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetColors(colors); mesh.SetTriangles(idx, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(n, 1, n));
            return mesh;
        }

        // A sphere seen from inside, every vertex on tile 0 (a solid one) so nothing is cut out
        static Mesh InsideOutSphere(float radius)
        {
            var src = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh s = src.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(src);
            var verts = new List<Vector3>(); s.GetVertices(verts);
            var normals = new List<Vector3>(); s.GetNormals(normals);
            for (int i = 0; i < verts.Count; i++) { verts[i] *= radius * 2f; normals[i] = -normals[i]; }
            var uvs = new List<Vector4>();
            var colors = new List<Color32>();
            for (int i = 0; i < verts.Count; i++) { uvs.Add(new Vector4(0.5f, 0.5f, 0, 0)); colors.Add(new Color32(255, 255, 255, 255)); }
            int[] tris = s.triangles;
            for (int i = 0; i < tris.Length; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            var m = new Mesh();
            m.SetVertices(verts); m.SetNormals(normals); m.SetUVs(0, uvs); m.SetColors(colors); m.triangles = tris;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 2f);
            return m;
        }

        // A Mobile/Skybox (as Blockbuster's sky is) whose six faces each have their own colour and a gradient
        // across and up, so a face in the wrong place, turned or flipped shows
        Material SixSidedSky()
        {
            var mat = Keep(new Material(Shader.Find("Mobile/Skybox")));
            string[] faces = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };
            Color[] tints = { new(1f, .3f, .3f), new(.3f, 1f, .3f), new(.3f, .3f, 1f), new(1f, 1f, .3f), new(.3f, 1f, 1f), new(1f, .3f, 1f) };
            for (int f = 0; f < 6; f++)
            {
                var t = Keep(new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
                var px = new Color[64 * 64];
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    px[y * 64 + x] = Color.Lerp(tints[f] * 0.25f, tints[f], x / 63f) * (0.5f + 0.5f * y / 63f);
                t.SetPixels(px);
                t.Apply();
                mat.SetTexture(faces[f], t);
            }
            return mat;
        }

        static Color32[] SheetPixels(out int width)
        {
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(Path.GetFullPath(SheetPath)));
            width = t.width;
            var px = t.GetPixels32();
            Object.DestroyImmediate(t);
            return px;
        }

        static float Solid(Color32[] px)
        {
            int n = 0;
            foreach (var p in px) if (p.a >= 128) n++;
            return (float)n / px.Length;
        }

        static int Diff(Color32 a, Color32 b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

        static float MeanDiff(Color32[] a, Color32[] b, int y0, int y1)
        {
            double sum = 0;
            int n = 0;
            for (int y = Mathf.Max(0, y0); y < Mathf.Min(Size, y1); y++)
            for (int x = 0; x < Size; x++)
            {
                Color32 p = a[y * Size + x], q = b[y * Size + x];
                sum += (Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b)) / 3.0;
                n++;
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }
    }
}
