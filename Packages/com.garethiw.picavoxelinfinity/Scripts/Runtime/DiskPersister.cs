using System;
using System.Net;
using System.IO;
using UnityEngine;

namespace PicaVoxel
{
    public class DiskPersister : MonoBehaviour, I_VoxelDataPersister
    {
        public string WorldName;
        
        private bool _isReady = true;

        public bool IsReady => _isReady;

        private string _basePath;
        
        private void OnEnable()
        {
            _basePath = Path.Combine(Application.persistentDataPath, "PicaVoxel", $"{WorldName}");
            if(!Directory.Exists(_basePath))
                Directory.CreateDirectory(_basePath);
        }

        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data)
        {
            Debug.Log($"DiskPersister SaveChunk ({WorldName}_{vol.Identifier}_{x}_{y}_{z}) byte length: {data.Length}");
            
            string fn = Path.Combine(_basePath, $"{vol.Identifier}_{x}_{y}_{z}.chunk");

            try
            {
                File.WriteAllBytes(fn, data);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }

            return true;
        }

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            Debug.Log($"DiskPersister LoadChunk ({WorldName}_{vol.Identifier}_{x}_{y}_{z})");

            string fn = Path.Combine(_basePath, $"{vol.Identifier}_{x}_{y}_{z}.chunk");

            if (!File.Exists(fn))
                return false;
            
            byte[] data;
            try
            {
                data = File.ReadAllBytes(fn);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }

            Chunk c = vol.GetChunk((x, y, z));
            if (!c)
                return false;
            
            c.LoadChanges(data);

            return true;
        }
    }
}