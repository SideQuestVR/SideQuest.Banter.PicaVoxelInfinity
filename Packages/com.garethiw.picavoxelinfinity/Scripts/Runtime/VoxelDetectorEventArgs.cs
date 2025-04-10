using System;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    [Serializable, Inspectable]
    public class VoxelDetectorEventArgs
    {
        public VoxelDetectorEventArgs()
        {
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
        [Inspectable] 
        public string DetectorName;
    }
}