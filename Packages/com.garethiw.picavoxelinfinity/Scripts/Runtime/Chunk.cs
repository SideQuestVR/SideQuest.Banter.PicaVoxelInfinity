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
#if UNITY_WINRT && !UNITY_EDITOR
using System.Threading.Tasks;
#endif
using UnityEngine.UI;

namespace PicaVoxel
{
    [AddComponentMenu("")]
    public class Chunk : MonoBehaviour
    {
        public Volume Volume
        {
            get
            {
                if (_volume) return _volume;
                return _volume = GetComponentInParent<Volume>();
            }
        }
        private Volume _volume;

        public (int x, int y, int z) Position;
        

        public void Initialize((int, int, int) pos)
        {
            Position = pos;
            _isDirty = true;
        }
        
        private bool _isDirty = false;
        
        // Calculation stuff ////////////////////////////////////////////
        private enum ChunkStatus
        {
            NoChange,
            CalculatingMesh,
            Ready
        }
        private ChunkStatus status = ChunkStatus.NoChange;

        private List<Vector3> vertices = new List<Vector3>();
        private List<Vector4> uvs = new List<Vector4>();
        private List<Color32> colors = new List<Color32>();
        private List<int> indexes = new List<int>();

        private MeshFilter mf;
        private MeshCollider mc;

        private bool hasCreatedRuntimeMesh = false;
        private bool hasCreatedRuntimeColMesh = false;
        private bool updateColliderNextFrame = false;
        
        private void Update()
        {
            if (status == ChunkStatus.Ready)
            {
                status = ChunkStatus.NoChange;
                if (!_isDirty)
                {
                    SetMesh();
                }
                else
                {
                    _isDirty = false;
                    GenerateMesh(false);
                }
            }

            if (updateColliderNextFrame)
            {
                UpdateCollider();
            }
        }
        
        private void GenerateMesh(bool immediate)
        {
            if (mf == null) mf = GetComponent<MeshFilter>();
            if (mc == null) mc = GetComponent<MeshCollider>();
            if (immediate)
            {
                Generate();
                SetMesh();

                if (_volume.CollisionMode != CollisionMode.None)
                {
                    Generate(ref voxels, voxelSize, overlapAmount, xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                            selfShadeIntensity, mode);
                    SetColliderMesh();
                }
                
                    
            }
            else
            {
                if (status != ChunkStatus.NoChange)
                {
                    _isDirty = true;
                    recalcToken = new RecalculateToken()
                    {
                        voxels = voxels,
                        voxelSize = voxelSize,
                        overlapAmount = overlapAmount,
                        xOffset = xOffset,
                        yOffset = yOffset,
                        zOffset = zOffset,
                        xSize = xSize,
                        ySize = ySize,
                        zSize = zSize,
                        ub0 = ub0,
                        ub1 = ub1,
                        ub2 = ub2,
                        selfShadeIntensity = selfShadeIntensity,
                        mode = mode,
                        colliderMode = colliderMode,
                        immediate = immediate, 
                    };
                    return;
                }
    
                if(!ThreadPool.QueueUserWorkItem(delegate
                    {
                        GenerateThreaded(ref voxels, voxelSize, overlapAmount, xOffset, yOffset, zOffset, xSize, ySize, zSize,ub0, ub1, ub2,
                            selfShadeIntensity, mode);
                    })
                ) 
                    Generate(ref voxels, voxelSize, overlapAmount, xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                                selfShadeIntensity, mode);

            }
        }
        
        private void GenerateThreaded(ref Voxel[] voxels, float voxelSize,float overlapAmount, int xOffset, int yOffset, int zOffset, int xSize, int ySize, int zSize, int ub0, int ub1, int ub2, float selfShadeIntensity, MeshingMode meshMode)
        {
            
            status = ChunkStatus.CalculatingMesh;

            Generate(ref voxels, voxelSize, overlapAmount, xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                        selfShadeIntensity, meshMode);
         
            status = ChunkStatus.Ready;
        }
        
        private void Generate(ref Voxel[] voxels, float voxelSize,float overlapAmount, int xOffset, int yOffset, int zOffset, int xSize, int ySize, int zSize, int ub0, int ub1, int ub2, float selfShadeIntensity, MeshingMode meshMode)
        {
           switch (meshMode)
            {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref voxels, voxelSize, overlapAmount,xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                        selfShadeIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref voxels, voxelSize, overlapAmount,xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                        selfShadeIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref voxels, voxelSize, xOffset, yOffset, zOffset, xSize, ySize, zSize, ub0, ub1, ub2,
                        selfShadeIntensity);
                    break;
            }    
        }

        private void SetMesh()
        {
            Volume vol = transform.parent.parent.parent.GetComponent<Volume>();
            if (vol == null) return;

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
                if (mc != null && vol.CollisionMode != CollisionMode.None)
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

            mf.sharedMesh.Clear();
            mf.sharedMesh.SetVertices(vertices);
            mf.sharedMesh.SetColors(colors);
            mf.sharedMesh.SetUVs(0, uvs);
            mf.sharedMesh.SetTriangles(indexes, 0);
            
            mf.sharedMesh.RecalculateNormals();

            mf.GetComponent<Renderer>().sharedMaterial = vol.Material;

            if (mc != null && vol.CollisionMode != CollisionMode.None)
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
            Volume vol = transform.parent.parent.parent.GetComponent<Volume>();
            if (vol == null) return;

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
