using System;
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
            EventBus.Trigger("OnSaveChunkChanges", args);

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
            EventBus.Trigger("OnLoadChunkChanges", args);

            return true;
        }
    }
}