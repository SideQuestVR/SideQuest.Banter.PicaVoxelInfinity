using System;
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
        
        private FastNoiseLite _noise;

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

            voxel.Color = Color.white;
            float val = _noise.GetNoise(x, z);
            val = (val + 1) * 0.5f;
            // if(Random.Range(0,100)==0)
            //     Debug.Log(val);
            val *= (HeightMax-HeightMin);
            //Debug.Log(val);
            voxel.State = (byte)(y < HeightMin + val?1:0);
            voxel.Value = y<=BedrockHeight? (byte)4: ((y+1) < HeightMin +val) ? (byte)1 : (byte)0;

            return true;
        }
    }
    
}