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

            byte[] payload = new byte[12 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(z), 0, payload, 8, 4);
            Buffer.BlockCopy(data, 0, payload, 12, data.Length);

            try
            {
                File.WriteAllBytes(fn, payload);
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

            if (data.Length < 12)
            {
                Debug.LogWarning($"DiskPersister LoadChunk ({WorldName}_{vol.Identifier}_{x}_{y}_{z}) data size {data.Length} is less than 12 bytes header.");
                return false;
            }

            int cx = BitConverter.ToInt32(data, 0);
            int cy = BitConverter.ToInt32(data, 4);
            int cz = BitConverter.ToInt32(data, 8);

            if (cx != x || cy != y || cz != z)
            {
                Debug.LogError($"DiskPersister LoadChunk coordinate mismatch! Expected ({x},{y},{z}) but got ({cx},{cy},{cz}) in file {fn}");
                return false;
            }

            Chunk c = vol.GetChunk((x, y, z));
            if (!c)
                return false;
            
            byte[] strippedData = new byte[data.Length - 12];
            Buffer.BlockCopy(data, 12, strippedData, 0, data.Length - 12);

            c.LoadChanges(strippedData);

            return true;
        }
    }
}