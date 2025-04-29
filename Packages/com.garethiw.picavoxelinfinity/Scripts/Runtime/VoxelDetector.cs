using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;

namespace PicaVoxel
{
    public class VoxelDetector : MonoBehaviour
    {
        public float DetectionInterval = 0.1f;
        public bool DetectInactiveVoxels = false;
        public bool FireOnlyWhenVoxelPositionChanges = false;
        public bool FireOnlyWhenVoxelValueOrStateChanges = true;
        public Volume Volume;

        public UnityEvent<VoxelDetectorEventArgs> OnVoxelDetected;

        private Voxel _lastVoxelDetected;
        private (int x, int y, int z) _lastVoxelPosition;
        private float _detectTime;

        private void Update()
        {
            _detectTime += Time.deltaTime;
            if (_detectTime >= DetectionInterval)
            {
                _detectTime = 0;

                DoDetection();
            }
        }

        private void DoDetection()
        {
            Voxel? v = Volume.GetVoxelAtWorldPosition(transform.position, out Chunk c, out (int x, int y, int z) pos);
            if (!v.HasValue)
                return;

            if (!DetectInactiveVoxels && !v.Value.Active)
                return;

            if (FireOnlyWhenVoxelValueOrStateChanges && v.Value.Value == _lastVoxelDetected.Value &&
                v.Value.State == _lastVoxelDetected.State)
                return;

            if (FireOnlyWhenVoxelPositionChanges && pos == _lastVoxelPosition)
                return;

            VoxelDetectorEventArgs vd = new VoxelDetectorEventArgs()
            {
                VolumeId = c.Volume.Identifier,
                ChunkX = c.Position.x,
                ChunkY = c.Position.y,
                ChunkZ = c.Position.z,
                VoxelX = pos.x,
                VoxelY = pos.y,
                VoxelZ = pos.z,
                VoxelValue = v.Value.Value,
                VoxelState = v.Value.State,
                VoxelColor = v.Value.Color,
                WorldPosition = transform.position,
                DetectorName = gameObject.name
            };

            _lastVoxelDetected = v.Value;
            _lastVoxelPosition = pos;

            //Debug.Log($"Voxel detected at {transform.position} with value {vd.VoxelValue} and state {vd.VoxelState} at voxel pos {pos}");

            OnVoxelDetected?.Invoke(vd);
            EventBus.Trigger("OnVoxelDetected", vd);
        }
    }
}