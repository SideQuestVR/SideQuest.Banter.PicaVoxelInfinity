namespace PicaVoxel
{
    public interface I_ChunkProcessor
    {
        public ProcessingSchedule Schedule { get; set; }
        public int Order { get; set; }
        
        public bool ProcessChunk(Volume volume, Chunk chunk);
    }

    public enum ProcessingSchedule
    {
        None,
        AfterGeneration,
        BeforeMeshing,
        OnTick
    }
}