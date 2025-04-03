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
        public bool UseLineRenderer = false;
        
        public InputAction AddAction;
        public InputAction RemoveAction;
        public InputAction IncrementValueAction;
        public InputAction DecrementValueAction;
        public InputAction ToggleActiveAction;

        public UnityEvent<VoxelEditEventArgs> OnManipulatorEdit;
        public UnityEvent<int> OnValueChanged;
        public UnityEvent<bool> OnActiveChanged;
        
        private RaycastHit _hitInfo;
        private Volume _selectedVolume;
        private Chunk _selectedChunk;
        private (int x, int y, int z) _selectedVoxel;
        private GameObject _cursor;
        private float _cursorUpdate = 0f;
        private int _lastAction = 0;
        private LineRenderer _lineRenderer;
        private Vector3 _lastLRPos;
        private float _lastVoxelSize = 1f;
        private int _orientation;
        
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

            if (UseLineRenderer)
            {
                _lineRenderer = GetComponent<LineRenderer>();
                _lineRenderer.enabled = false;
            }
            
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

        private void LateUpdate()
        {
            if (!IsActive)
            {
                _cursor.SetActive(false);
                return;
            }
            
            Ray ray = new Ray(transform.position, transform.forward);
            if (UseLineRenderer && _lineRenderer && _lineRenderer.enabled)
            {
                bool hit = Physics.Raycast(ray.origin, ray.direction, out _hitInfo, RayDistanceMinMax.y, LayerMask);
                if (hit)
                {
                    _lastLRPos = _hitInfo.point;
                    _lineRenderer.SetPosition(0, transform.position);
                    for (int i = 1; i < _lineRenderer.positionCount; i++)
                    {
                        _lineRenderer.SetPosition(i,
                            Vector3.Lerp(transform.position, _lastLRPos, (1f / _lineRenderer.positionCount) * (i + 1)));
                    }
                }
            }
        }

        private void Update()
        {
            if (!IsActive)
            {
                _cursor.SetActive(false);
                if (UseLineRenderer && _lineRenderer)
                    _lineRenderer.enabled = false;
                return;
            }

            Ray ray = new Ray(transform.position, transform.forward);
            
            _cursorUpdate += Time.deltaTime;
            if (_cursorUpdate < 0.1f)
                return;
            _cursorUpdate = 0f;
            
            _selectedVolume = VolumeRaycast(ray, _lastAction==0?-(0.001f*_lastVoxelSize):(0.001f*_lastVoxelSize), out _selectedChunk, out _selectedVoxel, out Vector3? hitPos);
            
            if (UseLineRenderer && _lineRenderer && hitPos.HasValue)
            {
                _lastLRPos = hitPos.Value;
                _lineRenderer.enabled = true;
            }
            
            if (_selectedVolume == null || _selectedChunk == null)
            {
                _cursor.SetActive(false);
                if (UseLineRenderer && _lineRenderer)
                    _lineRenderer.enabled = false;
                return;
            }
            
            Debug.DrawLine(ray.origin, hitPos.Value, Color.magenta, 0.5f);
            
            _lastVoxelSize = _selectedVolume.VoxelSize;

            _orientation = GetHitOrientation(_hitInfo, _selectedChunk.transform, ray.direction);
            Debug.Log($"Orientation: {_orientation}");
            
            _cursor.transform.localScale = new Vector3(1.01f,1.01f,1.01f) * _selectedVolume.VoxelSize;
            _cursor.transform.rotation = _selectedVolume.transform.rotation;
            _cursor.transform.position = _selectedChunk.transform.TransformPoint(new Vector3(_selectedVoxel.x, _selectedVoxel.y, _selectedVoxel.z)*_selectedVolume.VoxelSize + (Vector3.one * (_selectedVolume.VoxelSize * 0.5f)));
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

            if ((_hitInfo.point - transform.position).magnitude < (RayDistanceMinMax.x*_lastVoxelSize))
                return false;
            
            if (vol.GetVoxelAtWorldPosition(_hitInfo.point + (ray.direction * (0.1f*_lastVoxelSize)), out Chunk _, out (int x, int y, int z) _) == null)
                return false;

            // Custom blocks have state > 1, 2-5 is block orientation north,east,south,west
            byte state = 1;
            if (_selectedVolume.CustomBlocksDict.TryGetValue(VoxelValue, out CustomBlockData data))
            {
                state = 2;
                if(data.AllowOrientation)
                    state += (byte)GetHitOrientation(_hitInfo, _selectedChunk.transform, ray.direction);
            }
            
            Voxel? v = vol.SetVoxelAtWorldPosition(_hitInfo.point - (ray.direction * (0.1f*_lastVoxelSize)), new Voxel(){State = state, Value = VoxelValue, Color=VoxelColor}, out Chunk chunk, out (int x, int y, int z) pos);

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
                    voxelState: v.Value.State,
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

            Voxel? v = vol.SetVoxelAtWorldPosition(_hitInfo.point + (ray.direction *(0.1f*_lastVoxelSize)), new Voxel(){State = 0}, out Chunk chunk, out (int x, int y, int z) pos);

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
                        voxelState: v.Value.State,
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
            
            bool hit = Physics.Raycast(ray.origin, ray.direction, out _hitInfo, RayDistanceMinMax.y, LayerMask);
            if (hit)
            {
                return _hitInfo.collider.gameObject.GetComponentInParent<Volume>();
            }
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.red, 0.5f);
            return null;
        }
        
        private Volume VolumeRaycast(Ray ray, float offset, out Chunk chunk, out (int x, int y, int z) voxelPos, out Vector3? hitPos)
        {
            Volume vol = null;
            chunk = null;
            voxelPos = (0, 0, 0);
            hitPos = null;
            
            bool hit = Physics.Raycast(ray.origin, ray.direction, out _hitInfo, RayDistanceMinMax.y, LayerMask);
            if (hit)
            {
                hitPos = _hitInfo.point;
                vol = _hitInfo.collider.gameObject.GetComponentInParent<Volume>();
                if (vol == null)
                    return vol;

                vol.GetVoxelAtWorldPosition(_hitInfo.point+ (ray.direction *offset), out chunk, out voxelPos);
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
            SetVoxelValue(VoxelValue+1);
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
            SetVoxelValue(VoxelValue-1);
        }
        
        public void SetVoxelValue(int val)
        {
            if(!IsActive) SetActive(true);
            VoxelValue = (byte)(val % (MaxValue+1));
            OnValueChanged?.Invoke(VoxelValue);
            EventBus.Trigger("OnVoxelManipulatorValueChanged", (int)VoxelValue);
        }
        
        private int GetHitOrientation(RaycastHit hit, Transform localReference, Vector3 fallbackDirection)
        {
            // Transform the hit normal into local space
            Vector3 localNormal = localReference.InverseTransformDirection(hit.normal);

            // Reverse it to get the direction of the impact
            Vector3 impactDirection = -localNormal;

            // Check if the normal is mostly vertical (up/down)
            if (Mathf.Abs(impactDirection.y) > Mathf.Max(Mathf.Abs(impactDirection.x), Mathf.Abs(impactDirection.z)))
            {
                // Transform fallback direction into local space
                Vector3 localFallback = localReference.InverseTransformDirection(fallbackDirection);

                // Use the fallback instead
                impactDirection = new Vector3(localFallback.x, 0, localFallback.z).normalized;
            }

            // Determine the primary axis for horizontal directions
            float angleX = Mathf.Abs(impactDirection.x);
            float angleZ = Mathf.Abs(impactDirection.z);

            if (angleX > angleZ)
            {
                return (impactDirection.x > 0) ? 1 : 3; // East or West
            }
            else
            {
                return (impactDirection.z > 0) ? 0 : 2; // North or South
            }
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