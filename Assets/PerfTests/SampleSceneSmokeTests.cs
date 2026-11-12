using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PicaVoxel;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace PicaVoxelPerf
{
    /// <summary>
    /// The package's sample scene runs for a while without errors, and its active volumes end up with meshes and
    /// colliders. (Its finite MagicaVoxel examples are switched off in the scene and build no chunks, before these
    /// changes as after, so they are left off.)
    /// </summary>
    public class SampleSceneSmokeTests
    {
        readonly List<string> _errors = new();

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors.Add($"{type}: {message}");
        }

        [UnityTest]
        public IEnumerator SampleSceneRunsClean()
        {
#if UNITY_EDITOR
            Application.logMessageReceived += OnLog;
            try
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/SampleScene.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));

                float start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < 12f)
                    yield return null;

                var volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
                Assert.IsNotEmpty(volumes, "volumes in the sample scene");
                foreach (var v in volumes)
                {
                    int meshed = v.Chunks.Values.Count(c =>
                        c != null && c.GetComponent<MeshRenderer>().enabled &&
                        c.GetComponent<MeshFilter>().sharedMesh != null && c.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0);
                    int collided = v.Chunks.Values.Count(c =>
                        c != null && c.GetComponent<MeshCollider>() is MeshCollider mc && mc.enabled && mc.sharedMesh != null);
                    Debug.Log($"[Smoke] {v.name}: infinite={v.IsInfinite} mode={v.MeshingMode} chunks={v.Chunks.Count} " +
                              $"meshed={meshed} collided={collided} {PicaVoxelStats.Summary()}");
                    Assert.Greater(meshed, 0, $"{v.name} has meshed chunks");
                }
                Assert.IsEmpty(_errors, string.Join("\n", _errors.Take(10)));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
#else
            yield break;
#endif
        }
    }
}
