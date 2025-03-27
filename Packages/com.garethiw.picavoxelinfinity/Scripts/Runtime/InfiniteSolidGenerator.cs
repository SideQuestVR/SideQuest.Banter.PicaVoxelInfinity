using UnityEngine;

namespace PicaVoxel
{
    public class InfiniteSolidGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        private int _seed;
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }
        
        private bool _isReady = true;
        public bool IsReady => _isReady;

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            voxel.Color = Color.white;
            voxel.Active = true; //Random.Range(0,2)==1; //(x + y + z) % 2 == 0; //
            voxel.Value = 0;

            return true;
        }
    }
}