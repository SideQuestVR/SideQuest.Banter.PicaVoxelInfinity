﻿/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System.Threading;

namespace PicaVoxel
{
    public class Chunk : MonoBehaviour
    {
        public Voxel[] Voxels;
        
        public Volume Volume
        {
            get => _volume;
        }
        private Volume _volume;

        // Position of chunk in volume
        public (int x, int y, int z) Position;
        
        // Calculation stuff ////////////////////////////////////////////
        private enum ChunkStatus
        {
            NoChange,
            CalculatingMesh,
            Ready
        }

        // Is chunk free to be reused?
        public bool CanBeReused;
        
        private bool _isDataDirty = false;
        private bool _isChangesDirty = false;
        private bool _isMeshDirty = false;
        private ChunkStatus status = ChunkStatus.NoChange;

        private float _persistTime = 0f;
        
        private Dictionary<(int x, int y, int z), Voxel> _changes = new();

        private List<Vector3> vertices = new List<Vector3>(2048);
        private List<Vector4> uvs = new List<Vector4>(2048);
        private List<Color32> colors = new List<Color32>(2048);
        private List<int> indexes = new List<int>(2048);

        private MeshFilter mf;
        private MeshCollider mc;
        private MeshRenderer mr;

        private bool hasCreatedRuntimeMesh = false;
        private bool hasCreatedRuntimeColMesh = false;
        private bool updateColliderNextFrame = false;
        private bool _disableMrNextFrame= false;
        /////////////////////////////////////////////////////////////////
        
        public void Initialize((int, int, int) pos, Volume parentVolume, bool clear=true)
        {
            _volume = parentVolume;
            if (mf is null) mf = GetComponent<MeshFilter>();
            if (mr is null) mr = GetComponent<MeshRenderer>();
            if (mc is null) mc = GetComponent<MeshCollider>();
            mr.enabled = false;
            Position = pos;
            transform.localPosition = new Vector3((Position.x*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.y*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.z*Volume.ChunkSize)-(Volume.ChunkSize*0.5f)) * Volume.VoxelSize;
            gameObject.layer = Volume.ChunkLayer;
            if(Voxels==null || Voxels.Length==0 || clear)
                Voxels = new Voxel[Volume.ChunkSize * Volume.ChunkSize * Volume.ChunkSize];
            if (mf.sharedMesh is null)
                mf.sharedMesh = new Mesh();
            _isDataDirty = true;
            #if UNITY_EDITOR
            gameObject.name = $"Chunk {Position.x},{Position.y},{Position.z}";
            #endif
        }

        private bool _hasData = false;
        private void GenerateData()
        {
            if (!Volume.IsDataReady)
            {
                _isDataDirty = true;
                return;
            }

            _hasData = false;
            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is doing data gen");
            for (int z = 0; z < Volume.ChunkSize; z++)
                for (int y = 0; y < Volume.ChunkSize; y++)
                    for (int x = 0; x < Volume.ChunkSize; x++)
                    {
                        Volume.GenerateVoxel(x + (Volume.ChunkSize * Position.x), y + (Volume.ChunkSize * Position.y), z + (Volume.ChunkSize * Position.z), ref Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)]);
                        if(!_hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                            _hasData = true;
                    }
            
            foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
            {
                if (p.Schedule == ProcessingSchedule.AfterGeneration)
                    p.ProcessChunk(_volume, this);
            }
            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is finished data gen");

            _volume.LoadChunkChanges(Position);
            
            if (!_hasData)
                _disableMrNextFrame = true;
            else if (!Volume.IsInfinite)
                _isMeshDirty = true;
        }

        public Voxel? GetVoxel((int x, int y, int z) pos)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            return Voxels[i];
        }
        
        public Voxel? SetVoxel((int x, int y, int z) pos, Voxel newValue, bool persist = true)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            Voxels[i] = newValue;
            _changes[pos] = newValue;
            if (persist)
            {
                _persistTime = _volume.PersistenceInterval;
            }

            EvaluateData();
            GenerateMesh(true);
            
            for(int xx=0;xx<3;xx++)
                for(int yy=0;yy<3;yy++)
                    for (int zz = 0; zz < 3; zz++)
                    {
                        if (xx == Position.x && yy == Position.y && zz == Position.z)
                            continue;
                        
                        // Only re-mesh neighbouring chunk if edited voxel was along the adjacent edge
                        if((xx==0 && pos.x==0) ||
                           (xx==2 && pos.x==Volume.ChunkSize-1) ||
                           (zz==0 && pos.z==0) ||
                           (zz==2 && pos.z==Volume.ChunkSize-1) ||
                           (yy==0 && pos.y==0) ||
                           (yy==2 && pos.y==Volume.ChunkSize-1))
                            Volume.GetChunk((Position.x + (xx - 1), Position.y + (yy - 1), Position.z + (zz - 1)))?.SetMeshDirty();
                    }


            return Voxels[i];
        }
        
        public void SetVoxelByEditEvent(VoxelEditEventArgs e, bool persist = false)
        {
            Voxel v = new Voxel()
            {
                State = e.VoxelState,
                Value = e.VoxelValue,
                Color = e.VoxelColor
            };
            
            SetVoxel((e.VoxelX,e.VoxelY,e.VoxelZ), v, persist);
        }

        public void Persist()
        {
            if (_changes.Count == 0)
                return;

            if (_persistTime <= 0f)
                return;
            
            _persistTime -= Time.deltaTime;
            if (_persistTime <= 0f)
            {
                if (SaveChanges())
                {
                    _persistTime = 0;
                }
            }
        }
        
        public void CheckGeneration()
        {
            if (_isDataDirty)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty data");

                _isDataDirty = false;

                if (!ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            GenerateData();
                            // The volume will set the mesh to be regenerated once all chunks have queued data load
                            // if(!_hasData)
                            //     return;
                            // Thread.Sleep(25);
                            // GenerateMeshThreaded(Volume.MeshingMode);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    })
                   )
                    _isDataDirty = true;
            }
            
            if (_disableMrNextFrame)
            {
                mr.enabled = false;
                mc.enabled = false;
                _disableMrNextFrame= false;
            }
            
            if (status == ChunkStatus.NoChange && _isMeshDirty)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty mesh");
                _isMeshDirty = false;
                GenerateMesh(false);
            }
            
            if (status == ChunkStatus.Ready)
            {
                status = ChunkStatus.NoChange;
                if (!_isMeshDirty)
                {
                    SetMesh();
                    mr.enabled = true;
                }
                else
                {
                    _isMeshDirty = false;
                    GenerateMesh(false);
                }
            }
            
            if (updateColliderNextFrame)
            {
                UpdateCollider();
            }
        }
        
        public void GenerateMesh(bool immediate)
        {
            if (immediate)
            {
                GenerateMeshActual(Volume.MeshingMode);
                mr.enabled = _hasData;
                if (_hasData)
                {
                    SetMesh();
                    if (Volume.CollisionMode != CollisionMode.None)
                    {
                        if(Volume.MeshColliderMeshingMode!=Volume.MeshingMode)
                            GenerateMeshActual(Volume.MeshColliderMeshingMode);
                        UpdateCollider();
                    }
                }
                else
                {
                    mc.enabled = false;
                }
            }
            else
            {
                if (status != ChunkStatus.NoChange)
                {
                    _isMeshDirty = true;
                }
    
                if(!ThreadPool.QueueUserWorkItem(delegate
                    {
                        GenerateMeshThreaded(Volume.MeshingMode);
                    })
                ) 
                    GenerateMeshActual(Volume.MeshingMode);

            }
        }
        
        private void GenerateMeshThreaded(MeshingMode meshMode)
        {
            
            status = ChunkStatus.CalculatingMesh;

            GenerateMeshActual(meshMode);
         
            status = ChunkStatus.Ready;
        }

        private Chunk[] _neighbours = new Chunk[27];
        private void GenerateMeshActual(MeshingMode meshMode)
        {
            foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
            {
                if (p.Schedule == ProcessingSchedule.BeforeMeshing)
                    p.ProcessChunk(_volume, this);
            }
            
            for(int x=0;x<3;x++)
                for(int y=0;y<3;y++)
                    for(int z=0;z<3;z++)
                        _neighbours[x + 3 * (y + 3 * z)] = Volume.GetChunk((Position.x+(x-1),Position.y+(y-1),Position.z+(z-1)));
            
            switch (meshMode)
            {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref Voxels, this, _neighbours, Volume.CustomBlocksDict, Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
            }    
        }

        private void SetMesh()
        {
            if (vertices.Count == 0)
            {
                return;
            }

            mr.shadowCastingMode = Volume.CastShadows;
            mr.receiveShadows = Volume.ReceiveShadows;
 			mr.gameObject.isStatic = Volume.gameObject.isStatic;
            mr.sharedMaterial = Volume.Material;
            
            mf.sharedMesh.Clear();
            mf.sharedMesh.SetVertices(vertices);
            mf.sharedMesh.SetColors(colors);
            mf.sharedMesh.SetUVs(0, uvs);
            mf.sharedMesh.SetTriangles(indexes, 0);
            mf.sharedMesh.RecalculateNormals();

            if (Volume.CollisionMode != CollisionMode.None)
            {
                updateColliderNextFrame = true;
            }
        }

        private void UpdateCollider()
        {
            mf.sharedMesh.RecalculateBounds();
            mc.sharedMesh = mf.sharedMesh;
            mc.enabled = true;
            updateColliderNextFrame = false;
        }

        public void SetMeshDirty()
        {
            if (!_hasData)
                return;
            _isMeshDirty = true;
        }

        private void EvaluateData()
        {
            _hasData = false;
            for (int z = 0; z < Volume.ChunkSize; z++)
                for (int y = 0; y < Volume.ChunkSize; y++)
                    for (int x = 0; x < Volume.ChunkSize; x++)
                    {
                        if (!_hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                        {
                            _hasData = true;
                            return;
                        }
                    }
        }
        
        public void LoadChanges(byte[] data)
        {
            int bytelength = (sizeof(int) * 3);
            byte[] buff = new byte[bytelength];
            byte[] vbuff = new byte[Voxel.BYTE_SIZE];
            
            using (MemoryStream ms = new MemoryStream(data))
            {
                using (BinaryReader br = new BinaryReader(ms))
                {
                    while (ms.Position < ms.Length)
                    {
                        int x = br.ReadInt32();
                        int y = br.ReadInt32();
                        int z = br.ReadInt32();
                            
                        int l = ms.Read(vbuff, 0, Voxel.BYTE_SIZE);
                        if (l == Voxel.BYTE_SIZE)
                        {
                            Voxel v = new Voxel(vbuff);
                            Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)] = v;
                            _changes[(x, y, z)] = v;
                        }
                        
                    }
                }
            }
            
            EvaluateData();
            _isMeshDirty = true;
        }
        
        private bool SaveChanges()
        {
            if (!_volume.IsPersisterReady)
                return false;

            byte[] outbytes;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    foreach(KeyValuePair<(int x, int y, int z), Voxel> change in _changes)
                    {
                        bw.Write(change.Key.x);                        
                        bw.Write(change.Key.y);                        
                        bw.Write(change.Key.z);
                        bw.Write(change.Value.ToBytes());
                    }
                    bw.Flush();
                }

                outbytes = ms.ToArray();
            }

            return _volume.SaveChunkChanges(Position, outbytes);
        }
    }
}
