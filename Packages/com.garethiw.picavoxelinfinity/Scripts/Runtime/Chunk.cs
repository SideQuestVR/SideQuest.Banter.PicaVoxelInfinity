/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
// 
// Source code distributed under standard Asset Store licence:
// http://unity3d.com/legal/as_terms
//
/////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System.Collections;
using System.Threading;
using UnityEngine.UI;

namespace PicaVoxel
{
    public class Chunk : MonoBehaviour
    {
        public Voxel[] Voxels;
        
        public Volume Volume
        {
            get
            {
                if (_volume) return _volume;
                return _volume = GetComponentInParent<Volume>();
            }
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

        private bool _isDataDirty = true;
        private bool _isMeshDirty = false;
        private ChunkStatus status = ChunkStatus.NoChange;

        private List<Vector3> vertices = new List<Vector3>();
        private List<Vector4> uvs = new List<Vector4>();
        private List<Color32> colors = new List<Color32>();
        private List<int> indexes = new List<int>();

        private MeshFilter mf;
        private MeshCollider mc;
        private MeshRenderer mr;

        private bool hasCreatedRuntimeMesh = false;
        private bool hasCreatedRuntimeColMesh = false;
        private bool updateColliderNextFrame = false;
        /////////////////////////////////////////////////////////////////
        
        public void Initialize((int, int, int) pos)
        {
            Position = pos;
            transform.position = new Vector3((Position.x*Volume.XChunkSize)-(Volume.XChunkSize*0.5f), (Position.y*Volume.YChunkSize)-(Volume.YChunkSize*0.5f), (Position.z*Volume.ZChunkSize)-(Volume.ZChunkSize*0.5f)) * Volume.VoxelSize;
            if(Voxels==null || Voxels.Length==0)
                Voxels = new Voxel[Volume.XChunkSize * Volume.YChunkSize * Volume.ZChunkSize];
            #if UNITY_EDITOR
            gameObject.name = $"Chunk {Position.x},{Position.y},{Position.z}";
            #endif
        }

        private void GenerateData()
        {
            if (!Volume.IsDataReady)
            {
                _isDataDirty = true;
                return;
            }

            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is doing data gen");
            for (int z = 0; z < Volume.ZChunkSize; z++)
                for (int y = 0; y < Volume.YChunkSize; y++)
                    for (int x = 0; x < Volume.XChunkSize; x++)
                        Volume.GenerateVoxel(x+(Volume.XChunkSize*Position.x),y+(Volume.YChunkSize*Position.y),z+(Volume.ZChunkSize*Position.z),ref Voxels[x + Volume.XChunkSize * (y + Volume.YChunkSize * z)]);
            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is finished data gen");

            _isMeshDirty = true;
        }

        public Voxel? GetVoxel((int x, int y, int z) pos)
        {
            int i = pos.x + Volume.XChunkSize * (pos.y + Volume.YChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            return Voxels[i];
        }
        
        private void Update()
        {
            if (_isDataDirty)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty data");

                _isDataDirty = false;
                
                if(!ThreadPool.QueueUserWorkItem(delegate
                       {
                           GenerateData();
                       })
                    ) 
                    GenerateData();
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
            if (mf == null) mf = GetComponent<MeshFilter>();
            if (mc == null) mc = GetComponent<MeshCollider>();
            if (mr == null) mr = GetComponent<MeshRenderer>();
            if (immediate)
            {
                GenerateMeshActual(Volume.MeshingMode);
                SetMesh();

                if (Volume.CollisionMode != CollisionMode.None)
                {
                    if(Volume.MeshColliderMeshingMode!=Volume.MeshingMode)
                        GenerateMeshActual(Volume.MeshColliderMeshingMode);
                    SetColliderMesh();
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
        
        private void GenerateMeshActual(MeshingMode meshMode)
        {
           switch (meshMode)
           {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref Voxels, this, Volume.VoxelSize, 0f,0, 0, 0, Volume.XChunkSize, Volume.YChunkSize, Volume.ZChunkSize, Volume.XChunkSize-1, Volume.YChunkSize-1, Volume.ZChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref Voxels, this,Volume.VoxelSize, 0f,0, 0, 0, Volume.XChunkSize, Volume.YChunkSize, Volume.ZChunkSize, Volume.XChunkSize-1, Volume.YChunkSize-1, Volume.ZChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref Voxels, this,Volume.VoxelSize, 0, 0, 0, Volume.XChunkSize, Volume.YChunkSize, Volume.ZChunkSize, Volume.XChunkSize-1, Volume.YChunkSize-1, Volume.ZChunkSize-1, Volume.SelfShadingIntensity);
                    break;
           }    
        }

        private void SetMesh()
        {
            if (vertices.Count == 0)
            {
                if (Application.isPlaying && !hasCreatedRuntimeMesh)
                {
                    if(mf.sharedMesh!=null)
                        mf.sharedMesh = (Mesh)Instantiate(mf.sharedMesh);
                    hasCreatedRuntimeMesh = true;
                }

                if (mf.sharedMesh != null)
                {
                    mf.sharedMesh.Clear();
                    mf.sharedMesh = null;
                }
                if (mc != null && Volume.CollisionMode != CollisionMode.None)
                {
                    mc.sharedMesh = null;
                }
                return;
            }

            if (mf.sharedMesh == null)
            {
                mf.sharedMesh = new Mesh();
            }

            if (Application.isPlaying && !hasCreatedRuntimeMesh)
            {
                mf.sharedMesh = (Mesh)Instantiate(mf.sharedMesh);
                hasCreatedRuntimeMesh = true;
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

            if (mc != null && Volume.CollisionMode != CollisionMode.None)
            {
                updateColliderNextFrame = true;
            }
        }

        private void UpdateCollider()
        {
            mc.sharedMesh = null;
            mf.sharedMesh.RecalculateBounds();
            mc.sharedMesh = mf.sharedMesh;
            updateColliderNextFrame = false;
        }

        private void SetColliderMesh()
        {
            if (vertices.Count == 0)
            {
                if (mc.sharedMesh != null)
                {
                    mc.sharedMesh.Clear();
                    mc.sharedMesh = null;
                }
                return;
            }

            if (mc.sharedMesh == null || (Application.isPlaying && !hasCreatedRuntimeColMesh))
            {
                mc.sharedMesh = new Mesh();
                if (Application.isPlaying) hasCreatedRuntimeColMesh = true;
            }
            mc.sharedMesh.Clear();
            mc.sharedMesh.SetVertices(vertices);
            mc.sharedMesh.SetColors(colors);
            mc.sharedMesh.SetUVs(0, uvs);
            mc.sharedMesh.SetTriangles(indexes, 0);

            mc.sharedMesh.RecalculateNormals();
            mc.sharedMesh.RecalculateBounds();
        }
    }
}
