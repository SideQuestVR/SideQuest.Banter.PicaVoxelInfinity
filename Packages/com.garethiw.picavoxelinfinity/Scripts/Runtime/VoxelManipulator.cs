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
        public enum ManipulatorMode
        {
            Value,
            Color
        }
        
        public bool IsActive;
        
        public Vector2 RayDistanceMinMax;
        public LayerMask LayerMask;
        
        public int MaxValue = 4;
        public bool IncludeInactiveInValueRange = true; 
        
        public byte VoxelValue = 0;
        public Color VoxelColor = Color.white;

        public GameObject CursorPrefab;
        
        public InputAction AddAction;
        public InputAction RemoveAction;
        public InputAction IncrementValueAction;
        public InputAction DecrementValueAction;
        public InputAction ToggleActiveAction;

        public UnityEvent<VoxelEditEventArgs> OnManipulatorEdit;
        public UnityEvent<int> OnValueChanged;
        public UnityEvent<bool> OnActiveChanged;
        
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
            IncrementValueAction.Enable();
            IncrementValueAction.performed += OnIncrementValueAction;
            DecrementValueAction.Enable();
            DecrementValueAction.performed += OnDecrementValueAction;
            ToggleActiveAction.Enable();
            ToggleActiveAction.performed += OnActiveAction;


            _cursor = Instantiate(CursorPrefab);
            _cursor.SetActive(false);
        }

        private void OnActiveAction(InputAction.CallbackContext obj)
        {
            SetActive(!IsActive);
        }

        private void OnIncrementValueAction(InputAction.CallbackContext obj)
        {
            IncrementValue();
        }
        
        private void OnDecrementValueAction(InputAction.CallbackContext obj)
        {
            DecrementValue();
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
            if (!IsActive)
            {
                _cursor.SetActive(false);
                return;
            }
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
            if (!IsActive)
                return false;
            
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
                VoxelEditEventArgs args = 
                new VoxelEditEventArgs(
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
                OnManipulatorEdit?.Invoke(args);
                EventBus.Trigger("OnVoxelManipulatorEdit", args);
            }

            return v!=null;
        }
        
        public bool RemoveVoxel()
        {
            if (!IsActive)
                return false;
            
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            Volume vol = VolumeRaycast(ray);
            if (!vol) return false;

            Voxel? v = vol.SetVoxelAtWorldPosition(_hits[0].point + (ray.direction * 0.05f), new Voxel(){Active = false}, out Chunk chunk, out (int x, int y, int z) pos);

            _lastAction = 1;
            
            Debug.Log("Manipulator RemoveVoxel");
            if (v != null)
            {
                VoxelEditEventArgs args = 
                    new VoxelEditEventArgs(
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
                OnManipulatorEdit?.Invoke(args);
                EventBus.Trigger("OnVoxelManipulatorEdit", args);
            }
            
            return v!=null;
        }

        private Volume VolumeRaycast(Ray ray)
        {
            
            int hits = Physics.SphereCastNonAlloc(ray.origin, 0.025f, ray.direction, _hits, RayDistanceMinMax.y, LayerMask);
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

        public void SetActive(bool active)
        {
            IsActive = active;
            OnActiveChanged?.Invoke(active);
            EventBus.Trigger("OnVoxelManipulatorActiveChanged", active);
        }
        
        public void IncrementValue()
        {
            if (!IsActive)
            {
                SetActive(true);
                return;
            }
            if (VoxelValue == MaxValue && IncludeInactiveInValueRange)
            {
                SetActive(false);
                VoxelValue=(byte)0;
                OnValueChanged?.Invoke(VoxelValue);
                EventBus.Trigger("OnVoxelManipulatorValueChanged", (int)VoxelValue);
                return;
            }
            SetVoxelValue(VoxelValue++);
        }
        public void DecrementValue()
        {
            if (!IsActive)
            {
                SetActive(true);
                return;
            }
            if (VoxelValue == 0 && IncludeInactiveInValueRange)
            {
                SetActive(false);
                VoxelValue=(byte)MaxValue;
                OnValueChanged?.Invoke(VoxelValue);
                EventBus.Trigger("OnVoxelManipulatorValueChanged", (int)VoxelValue);
                return;
            }
            SetVoxelValue(VoxelValue--);
        }
        
        public void SetVoxelValue(int val)
        {
            if(!IsActive) SetActive(true);
            VoxelValue = (byte)(VoxelValue % (MaxValue+1));
            OnValueChanged?.Invoke(VoxelValue);
            EventBus.Trigger("OnVoxelManipulatorValueChanged", (int)VoxelValue);
        }

        private void OnDestroy()
        {
            AddAction.performed -= OnAddAction;
            RemoveAction.performed -= OnRemoveAction;
            IncrementValueAction.performed -= OnIncrementValueAction;
            DecrementValueAction.performed -= OnDecrementValueAction;
        }
    }
}