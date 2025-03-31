using System;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    [Serializable, Inspectable]
    public class ChunkChangesEventArgs
    {
        public ChunkChangesEventArgs(string volumeId, int chunkX, int chunkY, int chunkZ, byte[] data)
        {
            VolumeId = volumeId;
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
            Data = data;
        }
        
        [Inspectable]
        public string VolumeId;
        [Inspectable]
        public int ChunkX;
        [Inspectable]
        public int ChunkY;
        [Inspectable]
        public int ChunkZ;
        [Inspectable]
        public byte[] Data;
    }
}