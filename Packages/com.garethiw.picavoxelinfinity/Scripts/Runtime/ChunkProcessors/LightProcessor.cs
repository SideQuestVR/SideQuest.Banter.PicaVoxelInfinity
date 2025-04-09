using UnityEngine;

namespace PicaVoxel
{
    public class LightProcessor : MonoBehaviour, I_ChunkProcessor
    {
        private ProcessingSchedule _schedule;
        private int _order;

        public ProcessingSchedule Schedule
        {
            get => _schedule;
            set => _schedule = value;
        }

        public int Order
        {
            get => _order;
            set => _order = value;
        }

        public bool ProcessChunk(Volume volume, Chunk chunk)
        {
            throw new System.NotImplementedException();
        }
    }
}