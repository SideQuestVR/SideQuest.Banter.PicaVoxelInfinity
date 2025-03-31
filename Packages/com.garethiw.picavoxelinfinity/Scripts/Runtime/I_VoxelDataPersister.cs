using UnityEngine;

namespace PicaVoxel
{
    public interface I_VoxelDataPersister
    {
        public bool IsReady { get; }
        
        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data);
        public bool LoadChunk(Volume vol, int x, int y, int z);
    }
}