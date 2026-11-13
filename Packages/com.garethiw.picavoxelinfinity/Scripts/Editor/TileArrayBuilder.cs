/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PicaVoxel.EditorTools
{
    /// <summary>
    /// Builds a Texture2DArray from a tile sheet for "PicaVoxel URP Infinite Terrain Tile Array": one slice per tile,
    /// any padding round the tiles cut off, and mips made for each tile on its own, so distant terrain can be
    /// filtered without tiles bleeding into each other. The sheet itself is only read.
    ///
    /// Slices are numbered as the shader looks them up: row * columns + column, rows counted from the bottom of the
    /// sheet (the mesher's uv.w) and columns from the left (uv.z).
    /// </summary>
    public static class TileArrayBuilder
    {
        public struct Settings
        {
            public int Columns;
            public int Rows;
            /// <summary>Texels of padding round each tile, on every side</summary>
            public int Padding;
            /// <summary>Alpha at or above this is solid (the shader's _Cutoff)</summary>
            public float AlphaCutoff;
            /// <summary>Keep the same share of each tile solid in every mip, so cut-out leaves don't thin out</summary>
            public bool PreserveCoverage;
        }

        /// <summary>Builds the array in memory (readable, point filtered, clamped, with mips).</summary>
        public static Texture2DArray Build(Texture2D sheet, Settings s)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));
            if (s.Columns < 1 || s.Rows < 1) throw new ArgumentException("Columns and rows must be at least 1");

            Color32[] pixels = ReadPixels(sheet, out int width, out int height);
            int cellW = width / s.Columns, cellH = height / s.Rows;
            int tileW = cellW - 2 * s.Padding, tileH = cellH - 2 * s.Padding;
            if (width % s.Columns != 0 || height % s.Rows != 0)
                throw new ArgumentException($"{width} x {height} doesn't divide into {s.Columns} x {s.Rows} tiles");
            if (tileW < 1 || tileH < 1)
                throw new ArgumentException($"Padding {s.Padding} leaves no tile in a {cellW} x {cellH} cell");
            if (tileW != tileH || !Mathf.IsPowerOfTwo(tileW))
                Debug.LogWarning($"[PicaVoxel] Tiles are {tileW} x {tileH}; square, power-of-two tiles get a full, even mip chain");

            bool srgb = IsSrgb(sheet);
            int mipCount = 1 + (int)Math.Floor(Math.Log(Math.Max(tileW, tileH), 2));
            int count = s.Columns * s.Rows;
            var array = new Texture2DArray(tileW, tileH, count, TextureFormat.RGBA32, mipCount, !srgb)
            {
                name = sheet.name + " Tile Array",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };

            var tile = new Color[tileW * tileH];
            for (int row = 0; row < s.Rows; row++)
            for (int col = 0; col < s.Columns; col++)
            {
                // Rows from the bottom, as the pixels are stored (and as the shader's uv.w counts them)
                int x0 = col * cellW + s.Padding, y0 = row * cellH + s.Padding;
                for (int y = 0; y < tileH; y++)
                for (int x = 0; x < tileW; x++)
                {
                    Color c = pixels[(y0 + y) * width + x0 + x];
                    tile[y * tileW + x] = srgb ? Linear(c) : c;
                }
                WriteSlice(array, row * s.Columns + col, tile, tileW, tileH, mipCount, srgb, s);
            }
            array.Apply(false, false);
            return array;
        }

        /// <summary>
        /// Builds the array and saves it as an asset. An existing asset at the path is updated in place (keeping
        /// its GUID, so materials using it keep it); the tile sheet is never changed.
        /// </summary>
        public static Texture2DArray BuildAsset(Texture2D sheet, Settings s, string assetPath)
        {
            Texture2DArray built = Build(sheet, s);
            var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                existing.name = Path.GetFileNameWithoutExtension(assetPath);
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }
            built.name = Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(built, assetPath);
            AssetDatabase.SaveAssets();
            return built;
        }

        /// <summary>Settings from a material using the tile sheet terrain shaders (_NumTiles, _TilePadding).</summary>
        public static Settings FromMaterial(Material m)
        {
            Vector4 n = m.HasProperty("_NumTiles") ? m.GetVector("_NumTiles") : new Vector4(1, 1);
            return new Settings
            {
                Columns = Mathf.Max(1, Mathf.RoundToInt(n.x)),
                Rows = Mathf.Max(1, Mathf.RoundToInt(n.y)),
                Padding = m.HasProperty("_TilePadding") ? Mathf.RoundToInt(m.GetFloat("_TilePadding")) : 0,
                AlphaCutoff = 0.5f,
                PreserveCoverage = true,
            };
        }

        public static string DefaultAssetPath(Texture2D sheet)
        {
            string sheetPath = AssetDatabase.GetAssetPath(sheet);
            string dir = string.IsNullOrEmpty(sheetPath) ? "Assets" : Path.GetDirectoryName(sheetPath).Replace('\\', '/');
            return $"{dir}/{sheet.name} Tile Array.asset";
        }

        // Each mip a 2x2 box of the one above, averaged in linear space (colour weighted by alpha, so clear texels
        // don't darken edges), then alpha scaled so the solid share of the tile stays as at full size
        private static void WriteSlice(Texture2DArray array, int slice, Color[] mip0, int w, int h, int mipCount, bool srgb, Settings s)
        {
            float coverage = Coverage(mip0, s.AlphaCutoff, 1f);
            bool hasClear = coverage < 1f;
            Color[] mip = mip0;
            for (int level = 0; level < mipCount; level++)
            {
                if (level > 0)
                {
                    int pw = w, ph = h;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                    mip = Downsample(mip, pw, ph, w, h);
                    if (s.PreserveCoverage && hasClear)
                        KeepCoverage(mip, w, h, coverage);
                }
                var outPixels = new Color32[mip.Length];
                for (int i = 0; i < mip.Length; i++)
                    outPixels[i] = srgb ? (Color32)Gamma(mip[i]) : (Color32)mip[i];
                array.SetPixels32(outPixels, slice, level);
            }
        }

        private static Color[] Downsample(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                Color sum = default;
                float alpha = 0f;
                int n = 0;
                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int sx = Math.Min(sw - 1, x * 2 + ox), sy = Math.Min(sh - 1, y * 2 + oy);
                    Color c = src[sy * sw + sx];
                    sum.r += c.r * c.a; sum.g += c.g * c.a; sum.b += c.b * c.a;
                    alpha += c.a;
                    n++;
                }
                dst[y * dw + x] = alpha > 0f
                    ? new Color(sum.r / alpha, sum.g / alpha, sum.b / alpha, alpha / n)
                    : new Color(0f, 0f, 0f, 0f);
            }
            return dst;
        }

        private static float Coverage(Color[] px, float cutoff, float scale)
        {
            int solid = 0;
            foreach (Color c in px)
                if (Mathf.Min(1f, c.a * scale) >= cutoff) solid++;
            return (float)solid / px.Length;
        }

        private static readonly float[] Bayer4 =
        {
            0, 8, 2, 10,
            12, 4, 14, 6,
            3, 11, 1, 9,
            15, 7, 13, 5,
        };

        // Makes the mip's most covered texels solid and the rest clear, as many solid as keeps the full-size tile's
        // share: averaged alpha alone can't (a leaf tile's 2x2 blocks are mostly a quarter or half covered, so the
        // cut-off gives either far more or far fewer). Ties are broken by an ordered dither, so the solid texels
        // stay spread out rather than clumping.
        private static void KeepCoverage(Color[] px, int w, int h, float coverage)
        {
            int solid = Mathf.RoundToInt(coverage * px.Length);
            var order = new int[px.Length];
            var score = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                order[i] = i;
                // The dither (under 0.2) never outweighs a real difference in coverage (steps of 0.25 or more)
                score[i] = px[i].a + 0.19f * Bayer4[(i / w % 4) * 4 + i % w % 4] / 16f;
            }
            Array.Sort(order, (a, b) => score[b].CompareTo(score[a]));
            for (int k = 0; k < order.Length; k++)
                px[order[k]].a = k < solid && px[order[k]].a > 0f ? 1f : 0f;
        }

        private static Color Linear(Color c) => new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
        private static Color Gamma(Color c) => new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g), Mathf.LinearToGammaSpace(c.b), c.a);

        private static bool IsSrgb(Texture2D t)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            return importer == null || importer.sRGBTexture;
        }

        // The sheet's texels exactly as authored: from its PNG/JPG file where there is one (no import settings
        // involved), otherwise copied off the GPU at full size
        private static Color32[] ReadPixels(Texture2D sheet, out int width, out int height)
        {
            string path = AssetDatabase.GetAssetPath(sheet);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    tmp.LoadImage(File.ReadAllBytes(Path.GetFullPath(path)));
                    width = tmp.width;
                    height = tmp.height;
                    return tmp.GetPixels32();
                }
                finally { UnityEngine.Object.DestroyImmediate(tmp); }
            }

            width = sheet.width;
            height = sheet.height;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                FilterMode f = sheet.filterMode;
                sheet.filterMode = FilterMode.Point;
                Graphics.Blit(sheet, rt);
                sheet.filterMode = f;
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }
    }

    public class TileArrayBuilderWindow : EditorWindow
    {
        private Material _fromMaterial;
        private Texture2D _sheet;
        private TileArrayBuilder.Settings _s = new() { Columns = 14, Rows = 6, Padding = 1, AlphaCutoff = 0.5f, PreserveCoverage = true };

        [MenuItem("Tools/PicaVoxel/Tile Array Builder")]
        public static void Open() => GetWindow<TileArrayBuilderWindow>("Tile Array Builder");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Makes a Texture2DArray (one slice per tile, padding removed, mips per tile) from a tile sheet, for the " +
                "\"PicaVoxel URP Infinite Terrain Tile Array\" shader. The tile sheet is only read, never changed.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _fromMaterial = (Material)EditorGUILayout.ObjectField("Settings From Material", _fromMaterial, typeof(Material), false);
            if (EditorGUI.EndChangeCheck() && _fromMaterial != null)
            {
                _s = TileArrayBuilder.FromMaterial(_fromMaterial);
                if (_fromMaterial.HasProperty("_TileSheet"))
                    _sheet = _fromMaterial.GetTexture("_TileSheet") as Texture2D;
            }

            _sheet = (Texture2D)EditorGUILayout.ObjectField("Tile Sheet", _sheet, typeof(Texture2D), false);
            _s.Columns = EditorGUILayout.IntField("Columns", _s.Columns);
            _s.Rows = EditorGUILayout.IntField("Rows", _s.Rows);
            _s.Padding = EditorGUILayout.IntField("Padding (texels per side)", _s.Padding);
            _s.AlphaCutoff = EditorGUILayout.Slider("Alpha Cutoff", _s.AlphaCutoff, 0f, 1f);
            _s.PreserveCoverage = EditorGUILayout.Toggle("Preserve Cut-out Coverage", _s.PreserveCoverage);

            if (_sheet != null && _s.Columns > 0 && _s.Rows > 0)
            {
                int tw = _sheet.width / _s.Columns - 2 * _s.Padding, th = _sheet.height / _s.Rows - 2 * _s.Padding;
                EditorGUILayout.LabelField("Tiles", $"{_s.Columns * _s.Rows} of {tw} x {th}");
            }

            using (new EditorGUI.DisabledScope(_sheet == null))
            {
                if (GUILayout.Button("Build Tile Array..."))
                {
                    string def = TileArrayBuilder.DefaultAssetPath(_sheet);
                    if (!def.StartsWith("Assets/") || IsReadOnly(def))
                        def = "Assets/" + Path.GetFileName(def);
                    string path = EditorUtility.SaveFilePanelInProject("Save Tile Array", Path.GetFileNameWithoutExtension(def), "asset",
                        "Where to save the tile array", Path.GetDirectoryName(def));
                    if (!string.IsNullOrEmpty(path))
                    {
                        var array = TileArrayBuilder.BuildAsset(_sheet, _s, path);
                        EditorGUIUtility.PingObject(array);
                        Selection.activeObject = array;
                    }
                }
            }
        }

        private static bool IsReadOnly(string assetPath)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return info != null && info.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                   info.source != UnityEditor.PackageManager.PackageSource.Local;
        }
    }
}
