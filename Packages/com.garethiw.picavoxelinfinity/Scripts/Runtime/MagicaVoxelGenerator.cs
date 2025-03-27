using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CsharpVoxReader;
using CsharpVoxReader.Chunks;
using UnityEngine;
using UnityEngine.Networking;

namespace PicaVoxel
{
    public class MagicaVoxelGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        private int _seed;
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }
        
        private bool _isReady = false;
        public bool IsReady => _isReady;

        public string ModelURL;

        private byte[] _modelBytes;
        private Vector3Int _modelSize;
        private Voxel[] _voxels;
        
        private void Start()
        {
            StartCoroutine(GetRequest(ModelURL));
        }
        
        IEnumerator GetRequest(string uri)
        {
            Debug.Log($"Trying to download {uri}");
           
            using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
            {
                yield return webRequest.SendWebRequest();

                switch (webRequest.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                    case UnityWebRequest.Result.DataProcessingError:
                        Debug.LogError("Error: " + webRequest.error);
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        Debug.LogError("HTTP Error: " + webRequest.error);
                        break;
                    case UnityWebRequest.Result.Success:
                        _modelBytes = webRequest.downloadHandler.data;
                        ImportMagica();
                        break;
                }
            }
        }

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            if (x < 0 || x>=_modelSize.x || y < 0 || y>=_modelSize.y || z < 0 || z>=_modelSize.z)
                return false;
            
            var index = x + _modelSize.x * (y + _modelSize.y * z);
            if (index<0 || index >= _voxels.Length)
                return false;
            
            Voxel vox = _voxels[index];
            voxel.Value = vox.Value;
            voxel.Active = vox.Active;
            voxel.Color = vox.Color;


            return true;
        }
        
        private void ImportMagica()
        {
            Debug.Log($"Importing Magica model length {_modelBytes.Length} bytes");
            var loader = new MagicaLoader();
            
            using (MemoryStream stream = new MemoryStream(_modelBytes))
            {
                var reader = new VoxReader(stream, loader);
                reader.ReadFromStream();

                if (loader.Models.Count == 1)
                {
                    MagicaLoader.Model model = loader.Models[0];
                    _modelSize = new Vector3Int(model.Size.x, model.Size.y, model.Size.z);
                    _voxels = new Voxel[_modelSize.x * _modelSize.y * _modelSize.z];
                    
                    for(var x=0;x<model.Size.x;x++)
                        for(var y=0;y<model.Size.y;y++)
                            for(var z=0;z< model.Size.z;z++)
                                if(model.Data[x,y,z]!=0) 
                                    _voxels[x + _modelSize.x*(y + _modelSize.y*z)] = new Voxel()
                                    {
                                        Color = loader.Colors[model.Data[x,y,z]], 
                                        Active = true, 
                                        Value = 1
                                    };
                }
            }

            _isReady = true;
        }
        
        
        private class MagicaLoader : IVoxLoader
        {
            public enum GraphNodeType
            {
                Transform,
                Group,
                Shape
            }

            public class Model
            {
                public Vector3Int Size;
                public byte[,,] Data;
            }

            public class GraphNode
            {
                public GraphNodeType Type;
                public int Id;
                public Dictionary<string, byte[]> Attributes;
                public Dictionary<string, byte[]>[] AttributesArray;

                public List<int> ChildrenIds = new List<int>();

                public List<int> ModelIds = new List<int>();
                public List<GraphNode> Children = new List<GraphNode>();

                public GraphNode(GraphNodeType type, int id, Dictionary<string, byte[]> atts, Dictionary<string, byte[]>[] attsArray)
                {
                    Type = type;
                    Id = id;
                    Attributes = atts;
                    AttributesArray = attsArray;
                }
            }
            
            public List<Model> Models = new List<Model>();
            public Vector3Int Largest = Vector3Int.zero;

            public Color32[] Colors = new Color32[256];

            public List<GraphNode> SceneGraph = new List<GraphNode>();
            
            public MagicaLoader()
            {
                for (int i = 1; i < 256; i++)
                {
                    uint hexval = Palette.DefaultPalette[i];
                    byte cb = (byte)((hexval >> 16) & 0xFF);
                    byte cg = (byte)((hexval >> 8) & 0xFF);
                    byte cr = (byte)((hexval >> 0) & 0xFF);

                    Colors[i - 1] = new Color32(cr, cg, cb, 255);
                }
            }
            
            public void LoadModel(int sizeX, int sizeY, int sizeZ, byte[,,] data)
            {
                var size = new Vector3Int(sizeX,sizeY,sizeZ);
                if (size.x > Largest.x) Largest.x = size.x;
                if (size.y > Largest.y) Largest.y = size.y;
                if (size.z > Largest.z) Largest.z = size.z;
                
                Models.Add(new Model()
                {
                    Size =  size,
                    Data = data
                });
            }

            public void LoadPalette(uint[] palette)
            {
                for (int i = 1; i < palette.Length; i++)
                {
                    byte ca = (byte)((palette[i] >> 32) & 0xFF);
                    byte cr = (byte)((palette[i] >> 16) & 0xFF);
                    byte cg = (byte)((palette[i] >> 8) & 0xFF);
                    byte cb = (byte)((palette[i] >> 0) & 0xFF);

                    Colors[i] = new Color32(cr, cg, cb, ca);
                }
            }

            public void SetModelCount(int count)
            {
              //  throw new NotImplementedException();
            }

            public void SetMaterialOld(int paletteId, MaterialOld.MaterialTypes type, float weight, MaterialOld.PropertyBits property, float normalized)
            {
              //  throw new NotImplementedException();
            }

            public void NewTransformNode(int id, int childNodeId, int layerId, Dictionary<string, byte[]> attributes, Dictionary<string, byte[]>[] framesAttributes)
            {
                var newNode = new GraphNode(GraphNodeType.Transform, id, attributes, framesAttributes);
                newNode.ChildrenIds.Add(childNodeId);
                SceneGraph.Add(newNode);

            }

            public void NewGroupNode(int id, Dictionary<string, byte[]> attributes, int[] childrenIds)
            {
                var newNode = new GraphNode(GraphNodeType.Group, id, attributes, null);
                foreach(var c in childrenIds)
                    newNode.ChildrenIds.Add(c);
                SceneGraph.Add(newNode);

            }

            public void NewShapeNode(int id, Dictionary<string, byte[]> attributes, int[] modelIds, Dictionary<string, byte[]>[] modelsAttributes)
            {
                var newNode = new GraphNode(GraphNodeType.Shape, id, attributes, modelsAttributes);
                newNode.ModelIds = modelIds.ToList();
                // maybe do something with model attributes?
                SceneGraph.Add(newNode);
            }

            public void NewMaterial(int id, Dictionary<string, byte[]> attributes)
            {
              //  throw new NotImplementedException();
            }

            public void NewLayer(int id, Dictionary<string, byte[]> attributes)
            {
             //   throw new NotImplementedException();
            }
        }
    }
    
    
}