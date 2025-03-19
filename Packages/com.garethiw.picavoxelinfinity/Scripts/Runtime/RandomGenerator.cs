using UnityEngine;

namespace PicaVoxel
{
    public class RandomGenerator : I_VoxelDataGenerator
    {
        private int _seed;
        private System.Random _random;

        public int Seed
        {
            get => _seed;
            set
            {
                _seed = value; 
                _random = new System.Random(_seed);
            }
        }

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            voxel.Color = Color.white;
            voxel.Active = _random.Next(0,2)==1; //(x + y + z) % 2 == 0; //
            voxel.Value = 0;
        }
    }
}