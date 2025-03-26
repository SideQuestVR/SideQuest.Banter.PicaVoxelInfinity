using System;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    [Serializable, Inspectable]
    public class VoxelChangeEventArgs
    {
        public VoxelChangeEventArgs(string volumeId, int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ, bool voxelActive, byte voxelValue, Color voxelColor=new())
        {
            VolumeId = volumeId;
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
            VoxelX = voxelX;
            VoxelY = voxelY;
            VoxelZ = voxelZ;
            VoxelActive = voxelActive;
            VoxelValue = voxelValue;
            VoxelColor = voxelColor;
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
        public int VoxelX;
        [Inspectable]
        public int VoxelY;
        [Inspectable]
        public int VoxelZ;
        [Inspectable]
        public bool VoxelActive;
        [Inspectable]
        public byte VoxelValue;
        [Inspectable]
        public Color VoxelColor;
    }
}