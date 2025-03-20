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
        public float Scale = 0.25f;
        /////////////
        
        private FastNoiseLite _noise;

        private int _seaLevel;
        
        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            if (_noise == null)
            {
                _noise = new FastNoiseLite(seed: Seed);
                _noise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
                _noise.SetFrequency(0.1f);

                _seaLevel = (HeightMax - HeightMin) - HeightMin;
            }

            voxel.Color = Color.white;
            float val = _noise.GetNoise(x*Scale, z*Scale);
            val = (val + 1) * 0.5f;
            // if(Random.Range(0,100)==0)
            //     Debug.Log(val);
            val *= (HeightMax - HeightMin);
            //Debug.Log(val);
            voxel.Active = y < _seaLevel+ val;
            voxel.Value = y<=BedrockHeight? (byte)4: ((y+1) < _seaLevel+ val) ? (byte)1 : (byte)0;
        }
    }
    
}