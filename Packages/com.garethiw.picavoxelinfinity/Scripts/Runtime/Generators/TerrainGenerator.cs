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
        // Chunks generate on several threads at once, so the noise is set up once, under a lock
        private readonly object _noiseLock = new object();
        private volatile bool _noiseReady;

        // The noise only depends on the column (x, z), and chunks generate a column at a time (y innermost), so each
        // thread keeps the last column it worked out: one noise lookup a column instead of one a voxel.
        // (Compared with ReferenceEquals: this runs on worker threads, where Unity's == must not be used.)
        [ThreadStatic] private static TerrainGenerator t_owner;
        [ThreadStatic] private static int t_x, t_z;
        [ThreadStatic] private static float t_height;
        [ThreadStatic] private static float t_tree;
        [ThreadStatic] private static bool t_treeKnown;

        private void EnsureNoise()
        {
            if (_noiseReady)
                return;
            lock (_noiseLock)
            {
                if (_noiseReady)
                    return;

                var noise = new FastNoiseLite(seed: Seed);
                noise.SetNoiseType(FastNoiseLite.NoiseType.Value);
                noise.SetFrequency(0.05f);
                noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
                noise.SetDomainWarpAmp(50f);
                noise.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);
                noise.SetFractalGain(0.5f);
                noise.SetFractalOctaves(4);
                noise.SetFractalLacunarity(2.5f);

                var treeNoise = new FastNoiseLite(seed: TreeSeed);
                treeNoise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
                treeNoise.SetFrequency(0.5f);

                _noise = noise;
                _treeNoise = treeNoise;
                _noiseReady = true;
            }
        }

        private void Column(int x, int z)
        {
            if (ReferenceEquals(t_owner, this) && t_x == x && t_z == z)
                return;
            t_owner = this;
            t_x = x;
            t_z = z;
            t_height = (_noise.GetNoise(x, z) + 1) * 0.5f;
            t_treeKnown = false;
        }

        private float TreeAt(int x, int z)
        {
            if (!t_treeKnown)
            {
                float tree = +_treeNoise.GetNoise(x, z);
                t_tree = (tree + 1) * 0.5f;
                t_treeKnown = true;
            }
            return t_tree;
        }

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            EnsureNoise();
            Column(x, z);

            voxel.Color = Color.white;
            float val = t_height;
            // if(Random.Range(0,100)==0)
            //     Debug.Log(val);
            val *= (HeightMax-HeightMin);
            //Debug.Log(val);
            voxel.State = (byte)(y < HeightMin + val?1:0);
            voxel.Value = y<=BedrockHeight? (byte)4: ((y+1) < HeightMin +val) ? (byte)1 : (byte)0;

            if (!Trees)
                return true;

            int chx = Mathf.Abs(x % 16);
            int chz = Mathf.Abs(z % 16);

            if (y >= HeightMin + val && chx>=2 && chx<=13 && chz>=3 && chz<=13) // Should be using chunksize but we don't have volume here :/
            {
                float tree = TreeAt(x, z);
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
