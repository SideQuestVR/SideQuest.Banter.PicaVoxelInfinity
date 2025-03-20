namespace PicaVoxel
{
    public interface I_VoxelDataGenerator
    {
        public int Seed { get; set; }
        public bool IsReady { get; }
        
        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel);
    }
}