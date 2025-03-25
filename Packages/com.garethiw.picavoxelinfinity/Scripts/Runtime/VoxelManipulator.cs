using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PicaVoxel
{
    public class VoxelManipulator : MonoBehaviour
    {
        public Vector2 RayDistanceMinMax;
        public LayerMask LayerMask;

        public InputAction PerformAction;
        
        private RaycastHit[] _hits = new RaycastHit[1];

        private void Start()
        {
            PerformAction.performed += OnPerformAction;
        }

        private void OnPerformAction(InputAction.CallbackContext obj)
        {
            RemoveVoxel();
        }

        private void Update()
        {
            
        }

        public bool AddVoxel()
        {
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            Volume vol = VolumeRaycast(ray);
            if (!vol) return false;

            if ((_hits[0].point - transform.position).magnitude < RayDistanceMinMax.x)
                return false;
            
            if (vol.GetVoxelAtWorldPosition(_hits[0].point + (ray.direction * 0.05f)) == null)
                return false;
            
            Voxel? v = vol.SetVoxelAtWorldPosition(_hits[0].point - (ray.direction * 0.05f), new Voxel(){Active = true, Value = 2, Color=Color.white});
            
            return v!=null;
        }
        
        public bool RemoveVoxel()
        {
            Ray ray = new Ray(transform.position, transform.forward);
            Debug.DrawRay(ray.origin, ray.direction * RayDistanceMinMax.y, Color.magenta, 0.5f);
            Volume vol = VolumeRaycast(ray);
            if (!vol) return false;

            Voxel? v = vol.SetVoxelAtWorldPosition(_hits[0].point + (ray.direction * 0.05f), new Voxel(){Active = false});
            
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
    }
}