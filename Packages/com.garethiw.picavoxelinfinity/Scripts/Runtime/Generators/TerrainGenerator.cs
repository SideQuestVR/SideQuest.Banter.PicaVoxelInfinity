using System;
using System.Net;
using UnityEngine;
using Random = System.Random;

namespace PicaVoxel
{
    public class TerrainGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        private int _seed;
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }

        private bool _isReady = true;
        public bool IsReady => _isReady;
        
        // Modifiers
        public int HeightMax = 44;
        public int HeightMin = 20;
        public int BedrockHeight = -32;
        /////////////

        /// Trees
        public bool Trees = true;
        public int TreeSeed = 1;
        public float TreeThreshold = 0.9f;
        public int MinTreeHeight = 8;
        public int MaxTreeHeight = 12;
        public int TreeTrunkValue = 3;
        public int TreeLeavesValue = 8;
        ///////////// 
        
        private FastNoiseLite _noise;
        private FastNoiseLite _treeNoise;

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            if (_noise == null)
            {
                _noise = new FastNoiseLite(seed: Seed);
                _noise.SetNoiseType(FastNoiseLite.NoiseType.Value);
                _noise.SetFrequency(0.05f);
                _noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
                _noise.SetDomainWarpAmp(50f);
                _noise.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);
                _noise.SetFractalGain(0.5f);
                _noise.SetFractalOctaves(4);
                _noise.SetFractalLacunarity(2.5f);
            }
            
            if (_treeNoise == null)
            {
                _treeNoise = new FastNoiseLite(seed: TreeSeed);
                _treeNoise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
                _treeNoise.SetFrequency(0.5f);
            }

            voxel.Color = Color.white;
            float val = _noise.GetNoise(x, z);
            val = (val + 1) * 0.5f;
            // if(Random.Range(0,100)==0)
            //     Debug.Log(val);
            val *= (HeightMax-HeightMin);
            //Debug.Log(val);
            voxel.State = (byte)(y < HeightMin + val?1:voxel.State);
            voxel.Value = y<=BedrockHeight? (byte)4: ((y+1) < HeightMin +val) ? (byte)1 : voxel.Value;

            if (!Trees)
                return true;
            
            if (y >= HeightMin + val)
            {
                float tree = +_treeNoise.GetNoise(x, z);
                tree = (tree + 1) * 0.5f;
                if (tree < TreeThreshold)
                    return true;

                if (y < HeightMin + val + MinTreeHeight)
                {
                    voxel.State = (byte)1;
                    voxel.Value = (byte)TreeTrunkValue;
                }
            }
            
            return true;
        }
    }
    
}