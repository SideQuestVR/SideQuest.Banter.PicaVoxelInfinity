using System;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    [Serializable, Inspectable]
    public class VoxelEditEventArgs
    {
        public VoxelEditEventArgs(string volumeId, int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ, byte voxelState, byte voxelValue, Vector3 worldPosition, Color voxelColor=new())
        {
            VolumeId = volumeId;
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
            VoxelX = voxelX;
            VoxelY = voxelY;
            VoxelZ = voxelZ;
            VoxelState = voxelState;
            VoxelValue = voxelValue;
            VoxelColor = voxelColor;
            WorldPosition = worldPosition;
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
        public byte VoxelState;
        [Inspectable]
        public byte VoxelValue;
        [Inspectable]
        public Color VoxelColor;
        [Inspectable] 
        public Vector3 WorldPosition;
    }
}