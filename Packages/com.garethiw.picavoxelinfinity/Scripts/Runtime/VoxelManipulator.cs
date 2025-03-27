using System;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace PicaVoxel
{
    public class VoxelManipulator : MonoBehaviour
    {
        public Vector2 RayDistanceMinMax;
        public LayerMask LayerMask;

        public int NumberOfTiles = 5;
        
        public byte VoxelValue = 0;
        public Color VoxelColor = Color.white;

        public GameObject CursorPrefab;
        
        [FormerlySerializedAs("PerformAction")] public InputAction AddAction;
        public InputAction RemoveAction;

        public UnityEvent<VoxelChangeEventArgs> OnManipulatorChange;
        
        private RaycastHit[] _hits = new RaycastHit[1];
        private Volume _selectedVolume;
        private Chunk _selectedChunk;
        private (int x, int y, int z) _selectedVoxel;
        private GameObject _cursor;
        private float _cursorUpdate = 0f;
        private int _lastAction = 0;
        
        private void Start()
        {
            AddAction.Enable();
            AddAction.performed += OnAddAction;
            RemoveAction.Enable();
            RemoveAction.performed += OnRemoveAction;

            _cursor = Instantiate(CursorPrefab);
            _cursor.SetActive(false);
        }

        private void OnAddAction(InputAction.CallbackContext obj)
        {
            AddVoxel();
        }
        
        private void OnRemoveAction(InputAction.CallbackContext obj)
        {
            RemoveVoxel();
        }

        private void Update()
        {
            _cursorUpdate += Time.deltaTime;
            if (_cursorUpdate < 0.1f)
                return;
            _cursorUpdate = 0f;
            
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            _selectedVolume = VolumeRaycast(ray, _lastAction==0?-0.05f:0.05f, out _selectedChunk, out _selectedVoxel);

            if (_selectedVolume == null || _selectedChunk == null)
            {
                _cursor.SetActive(false);
                return;
            }

            _cursor.transform.localScale = new Vector3(1.01f,1.01f,1.01f) * _selectedVolume.VoxelSize;
            _cursor.transform.rotation = _selectedVolume.transform.rotation;
            _cursor.transform.position = _selectedChunk.transform.position + new Vector3(_selectedVoxel.x, _selectedVoxel.y, _selectedVoxel.z)*_selectedVolume.VoxelSize + (Vector3.one * (_selectedVolume.VoxelSize * 0.5f));
            _cursor.SetActive(true);
        }

        public bool AddVoxel()
        {
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            Volume vol = VolumeRaycast(ray);
            if (!vol) return false;

            if ((_hits[0].point - transform.position).magnitude < RayDistanceMinMax.x)
                return false;
            
            if (vol.GetVoxelAtWorldPosition(_hits[0].point + (ray.direction * 0.05f), out Chunk _, out (int x, int y, int z) _) == null)
                return false;
            
            Voxel? v = vol.SetVoxelAtWorldPosition(_hits[0].point - (ray.direction * 0.05f), new Voxel(){Active = true, Value = VoxelValue, Color=VoxelColor}, out Chunk chunk, out (int x, int y, int z) pos);

            _lastAction = 0;

            Debug.Log("Manipulator AddVoxel");
            if (v != null)
            {
                VoxelChangeEventArgs args = 
                new VoxelChangeEventArgs(
                    volumeId: _selectedVolume.Identifier,
                    chunkX: _selectedChunk.Position.x,
                    chunkY: _selectedChunk.Position.y,
                    chunkZ: _selectedChunk.Position.z,
                    voxelX: pos.x,
                    voxelY: pos.y,
                    voxelZ: pos.z,
                    voxelActive: v.Value.Active,
                    voxelValue: v.Value.Value,
                    voxelColor: v.Value.Color
                );
                OnManipulatorChange?.Invoke(args);
                Debug.Log("Sending to eventbus");
                EventBus.Trigger("OnVoxelManipulatorChange", args);
            }

            return v!=null;
        }
        
        public bool RemoveVoxel()
        {
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            Volume vol = VolumeRaycast(ray);
            if (!vol) return false;

            Voxel? v = vol.SetVoxelAtWorldPosition(_hits[0].point + (ray.direction * 0.05f), new Voxel(){Active = false}, out Chunk chunk, out (int x, int y, int z) pos);

            _lastAction = 1;
            
            Debug.Log("Manipulator RemoveVoxel");
            if (v != null)
            {
                VoxelChangeEventArgs args = 
                    new VoxelChangeEventArgs(
                        volumeId: _selectedVolume.Identifier,
                        chunkX: _selectedChunk.Position.x,
                        chunkY: _selectedChunk.Position.y,
                        chunkZ: _selectedChunk.Position.z,
                        voxelX: pos.x,
                        voxelY: pos.y,
                        voxelZ: pos.z,
                        voxelActive: v.Value.Active,
                        voxelValue: v.Value.Value,
                        voxelColor: v.Value.Color
                    );
                OnManipulatorChange?.Invoke(args);
                Debug.Log("Sending to eventbus");
                EventBus.Trigger("OnVoxelManipulatorChange", args);
            }
            
            return v!=null;
        }

        private Volume VolumeRaycast(Ray ray)
        {
            
            int hits = Physics.SphereCastNonAlloc(ray.origin, 0.05f, ray.direction, _hits, RayDistanceMinMax.y, LayerMask);
            if (hits > 0)
            {
                return _hits[0].collider.gameObject.GetComponentInParent<Volume>();
            }
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.red, 0.5f);
            return null;
        }
        
        private Volume VolumeRaycast(Ray ray, float offset, out Chunk chunk, out (int x, int y, int z) voxelPos)
        {
            Volume vol = null;
            chunk = null;
            voxelPos = (0, 0, 0);
            
            int hits = Physics.SphereCastNonAlloc(ray.origin, 0.05f, ray.direction, _hits, RayDistanceMinMax.y, LayerMask);
            if (hits > 0)
            {
                vol = _hits[0].collider.gameObject.GetComponentInParent<Volume>();
                if (vol == null)
                    return vol;

                vol.GetVoxelAtWorldPosition(_hits[0].point+ (ray.direction *offset), out chunk, out voxelPos);
                return vol;
            }
            
            return null;
        }

        public void SetVoxelValue(int val)
        {
            VoxelValue = (byte)Math.Clamp(val, 0, NumberOfTiles);
        }

        private void OnDestroy()
        {
            AddAction.performed -= OnAddAction;
            RemoveAction.performed -= OnRemoveAction;
        }
    }
}