using System;
using System.Collections.Concurrent;
using System.Net;
using System.IO;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    public class VisualScriptingPersister : MonoBehaviour, I_VoxelDataPersister
    {
        private bool _isReady = true;

        public bool IsReady => _isReady;

        private string _basePath;

        // Chunks load from their data job, on a worker thread; graphs must only be triggered on the main thread
        private readonly ConcurrentQueue<(string hook, ChunkChangesEventArgs args)> _pending = new();

        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data)
        {
            ChunkChangesEventArgs args = 
                new ChunkChangesEventArgs(
                    volumeId: vol.Identifier,
                    chunkX: x,
                    chunkY: y,
                    chunkZ: z,
                    data: data
                );
            Trigger("OnSaveChunkChanges", args);

            return true;
        }

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            ChunkChangesEventArgs args = 
                new ChunkChangesEventArgs(
                    volumeId: vol.Identifier,
                    chunkX: x,
                    chunkY: y,
                    chunkZ: z,
                    data: null
                );
            Trigger("OnLoadChunkChanges", args);

            return true;
        }

        private void Trigger(string hook, ChunkChangesEventArgs args)
        {
            if (Volume.IsMainThread)
                EventBus.Trigger(hook, args);
            else
                _pending.Enqueue((hook, args));
        }

        private void Update()
        {
            while (_pending.TryDequeue(out var e))
                EventBus.Trigger(e.hook, e.args);
        }
    }
}
