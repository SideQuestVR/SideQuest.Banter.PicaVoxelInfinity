namespace PicaVoxel
{
    public interface I_VoxelDataGenerator
    {
        public int Seed { get; set; }
        
        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel);
    }

    public enum GeneratorType
    {
        Solid,
        Random,
        Terrain
    }
}