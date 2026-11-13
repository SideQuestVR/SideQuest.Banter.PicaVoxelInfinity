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
    /// Renders a skybox material (6-sided, cubemap, panoramic or procedural) into a Cubemap asset, so the
    /// "PicaVoxel URP Infinite Terrain Tile Array" shader can fade distant terrain into exactly what the sky shows
    /// behind it. The skybox material and its textures are only read.
    /// </summary>
    public static class SkyboxCubemapBuilder
    {
        // Each cubemap face as a camera at the centre sees it: looking along the face's axis, its top edge the
        // face's up (Unity's cubemap layout: +X's right is -Z, +Y's right is +X and its top -Z, and so on)
        private static readonly (CubemapFace face, Vector3 forward, Vector3 up)[] Faces =
        {
            (CubemapFace.PositiveX, Vector3.right, Vector3.up),
            (CubemapFace.NegativeX, Vector3.left, Vector3.up),
            (CubemapFace.PositiveY, Vector3.up, Vector3.back),
            (CubemapFace.NegativeY, Vector3.down, Vector3.forward),
            (CubemapFace.PositiveZ, Vector3.forward, Vector3.up),
            (CubemapFace.NegativeZ, Vector3.back, Vector3.up),
        };

        // A layer no scene object is on (the camera must cull nothing but the sky, but with an empty culling mask
        // URP renders the sky through a different path, a gamma step away from what the game's camera shows)
        private const int EmptyLayer = 31;

        /// <summary>Renders the skybox into a new Cubemap (sRGB, with mips).</summary>
        public static Cubemap Render(Material skybox, int faceSize)
        {
            if (skybox == null) throw new ArgumentNullException(nameof(skybox));
            faceSize = Mathf.ClosestPowerOfTwo(Mathf.Clamp(faceSize, 16, 2048));

            var camGo = new GameObject("PicaVoxel Skybox Capture") { hideFlags = HideFlags.HideAndDontSave };
            var rt = RenderTexture.GetTemporary(faceSize, faceSize, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var face = new Texture2D(faceSize, faceSize, TextureFormat.RGBA32, false);
            Material previous = RenderSettings.skybox;
            RenderTexture active = RenderTexture.active;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 1 << EmptyLayer;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.fieldOfView = 90f;
                cam.aspect = 1f;
                cam.nearClipPlane = 0.01f;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.targetTexture = rt;
                RenderSettings.skybox = skybox;

                // Explicitly sRGB: the rendered texels are gamma encoded, and read as linear they'd come out too bright
                var cube = new Cubemap(faceSize, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB,
                    UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain) { name = skybox.name + " Sky Cubemap" };
                foreach (var (cubeFace, forward, up) in Faces)
                {
                    cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(forward, up));
                    cam.Render();
                    RenderTexture.active = rt;
                    face.ReadPixels(new Rect(0, 0, faceSize, faceSize), 0, 0);
                    face.Apply(false);
                    Color[] px = face.GetPixels();
                    // Read back bottom row first; a cubemap face's pixels start at its top
                    px = FlipRows(px, faceSize);
                    // Opaque: cubemap skybox shaders can take alpha as an HDR multiplier
                    for (int i = 0; i < px.Length; i++) px[i].a = 1f;
                    cube.SetPixels(px, cubeFace);
                }
                cam.targetTexture = null;
                cube.Apply(true, false);
                return cube;
            }
            finally
            {
                RenderSettings.skybox = previous;
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(face);
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>Renders the skybox and saves it, updating an existing asset at the path in place.</summary>
        public static Cubemap RenderAsset(Material skybox, int faceSize, string assetPath)
        {
            Cubemap built = Render(skybox, faceSize);
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
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

        private static Color[] FlipRows(Color[] px, int size)
        {
            var o = new Color[px.Length];
            for (int y = 0; y < size; y++)
                Array.Copy(px, y * size, o, (size - 1 - y) * size, size);
            return o;
        }
    }

    public class SkyboxCubemapBuilderWindow : EditorWindow
    {
        private Material _skybox;
        private int _faceSize = 256;

        [MenuItem("Tools/PicaVoxel/Skybox Cubemap Builder")]
        public static void Open()
        {
            var w = GetWindow<SkyboxCubemapBuilderWindow>("Skybox Cubemap Builder");
            if (w._skybox == null) w._skybox = RenderSettings.skybox;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Renders a skybox into a cubemap for the tile array terrain shader's \"Sky Colour From: Cubemap\" fade, so " +
                "distant terrain fades into exactly what the sky shows behind it. The skybox itself is not changed.",
                MessageType.Info);
            _skybox = (Material)EditorGUILayout.ObjectField("Skybox Material", _skybox, typeof(Material), false);
            _faceSize = EditorGUILayout.IntPopup("Face Size", _faceSize, new[] { "64", "128", "256", "512", "1024" }, new[] { 64, 128, 256, 512, 1024 });
            EditorGUILayout.LabelField(" ", "The fade only shows the sky near the horizon, softly; 128-256 is plenty.", EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(_skybox == null))
            {
                if (GUILayout.Button("Build Sky Cubemap..."))
                {
                    string skyPath = AssetDatabase.GetAssetPath(_skybox);
                    string dir = string.IsNullOrEmpty(skyPath) || !skyPath.StartsWith("Assets/") ? "Assets" : Path.GetDirectoryName(skyPath);
                    string path = EditorUtility.SaveFilePanelInProject("Save Sky Cubemap", _skybox.name + " Sky Cubemap", "asset",
                        "Where to save the sky cubemap", dir);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var cube = SkyboxCubemapBuilder.RenderAsset(_skybox, _faceSize, path);
                        EditorGUIUtility.PingObject(cube);
                        Selection.activeObject = cube;
                    }
                }
            }
        }
    }
}
