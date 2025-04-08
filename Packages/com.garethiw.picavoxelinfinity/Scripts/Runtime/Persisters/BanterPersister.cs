using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    public class BanterPersister : MonoBehaviour, I_VoxelDataPersister
    {
        private bool _isReady = true;

        public bool IsReady => _isReady;
        
        private HttpClient _httpClient;

        public string SpaceName;
        
        public string BaseURL;
        private string _baseUrl;

        public int BatchGetSize = 100;
        public float BatchGetInterval = 0.1f;
        
        private float _batchInterval;
        private ConcurrentQueue<string> _chunksToFetch = new ConcurrentQueue<string>();

        private Volume _volume;
        
        private void OnEnable()
        {
            _baseUrl = BaseURL.TrimEnd('/');
            _httpClient = new HttpClient();
            _volume = GetComponent<Volume>();
        }

        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data)
        {
            string key = $"{SpaceName}_{vol.Identifier.Replace("{", "").Replace("}", "")}_{x}_{y}_{z}";

            _ = Task.Run(()=>PostDataAsync(key, data));

            return true;
        }

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            string key = $"{SpaceName}_{vol.Identifier.Replace("{", "").Replace("}", "")}_{x}_{y}_{z}";
            _chunksToFetch.Enqueue(key);
            
            return true;
        }

        private bool _ready = true;
        private void Update()
        {
            _batchInterval += Time.deltaTime;
            if (_batchInterval >= BatchGetInterval && _ready)
            {
                _batchInterval = 0;
                if (_chunksToFetch.Count > 0)
                {
                    StringBuilder batch = new StringBuilder();
                    for (int i = 0; i < BatchGetSize; i++)
                    {
                        if(_chunksToFetch.TryDequeue(out string key))
                        {
                            batch.Append(key);
                            batch.Append(",");
                        }
                    }
                    string keys = batch.ToString().TrimEnd(',');
                    
                    if (keys == string.Empty)
                        return;

                    _ready = false;
                    
                    _ = Task.Run(()=>GetDataAsync(keys)).ContinueWith((Task<byte[]> task) =>
                    {
                        if (task.IsFaulted || !task.IsCompletedSuccessfully || task.Result.Length == 0)
                        {
                            _ready = true;
                            return;
                        }

                        try
                        {
                            string[] keysplit = keys.Split(',');

                            using (MemoryStream stream = new MemoryStream(task.Result))
                            {
                                using (BinaryReader reader = new BinaryReader(stream))
                                {
                                    int n = 0;
                                    while (reader.BaseStream.Position < reader.BaseStream.Length)
                                    {
                                        int l = reader.ReadInt32();
                                        if (l == 0)
                                        {
                                            n++;
                                            continue;
                                        }
                                        
                                        byte[] data = reader.ReadBytes(l);

                                        string key = keysplit[n];
                                        string[] parts = key.Split('_');

                                        Debug.Log($"{n}: {key}");
                                        
                                        n++;
                                        
                                        Chunk c = _volume.GetChunk((int.Parse(parts[2]), int.Parse(parts[3]),
                                            int.Parse(parts[4])));
                                        if (!c)
                                            continue;

                                        c.LoadChanges(data);
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError(e);
                        }
                        
                        _ready = true;
                    });
                }
            }
        }

        public async Task<byte[]> GetDataAsync(string keys)
        {
            try
            {
                string url = $"{_baseUrl}/{keys}";
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in GetDataAsync: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> PostDataAsync(string key, byte[] data)
        {
            try
            {
                string url = $"{_baseUrl}/save/{key}";
                ByteArrayContent content = new ByteArrayContent(data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in PostDataAsync: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}