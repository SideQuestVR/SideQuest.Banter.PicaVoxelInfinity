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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using CsharpVoxReader;
using CsharpVoxReader.Chunks;
using JetBrains.Annotations;

namespace PicaVoxel
{
    public static class MagicaVoxelImporter
    {
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
        
        public static void FromMagica(FileStream stream, string importName, GameObject original, float voxelSize, int mode)
        {
            var loader = new MagicaLoader();
            var reader = new VoxReader(stream, loader);
            reader.ReadFromStream();

            Transform root = null;
            if (loader.Models.Count == 0) return;
            
            // If there's only one model, switch to single volume mode (better for re-importing)
            if (loader.Models.Count == 1) mode = 1;

            // An indexed reference of loaded models
            var volumes = new List<Volume>();
            
            Volume newVolume;
            switch (mode)
            {
                case 0:
                    // World mode

                    // Make the scenegraph recursive
                    foreach (var n in loader.SceneGraph)
                    {
                        foreach(var cn in n.ChildrenIds)
                            n.Children.Add(loader.SceneGraph[cn]);
                    }
                    
                    if (original==null)
                    {
                        // Root gameobject
                        var go= new GameObject();
                        root = go.transform;
                        go.AddComponent<MagicaVoxelImport>();
                        go.GetComponent<MagicaVoxelImport>().ImportedFile = stream.Name;
                        go.GetComponent<MagicaVoxelImport>().ImportedVoxelSize = voxelSize;
                        go.GetComponent<MagicaVoxelImport>().ImportedMode = mode;
                        go.name = importName != "MagicaVoxel Import" ? importName : Path.GetFileNameWithoutExtension(stream.Name);
                    }

                    if (original != null)
                    {
                        root = original.transform;
                        for (var i = root.childCount - 1; i >= 0; i--)
                            GameObject.DestroyImmediate(root.GetChild(i).gameObject);
                    }
                    
                    foreach (MagicaLoader.Model m in loader.Models)
                    {
                        newVolume = CreateVolume(m, loader.Models.IndexOf(m), loader.Colors, voxelSize);
                        if (root != null)
                        {
                            newVolume.transform.SetParent(root);
                        }
                        volumes.Add(newVolume);
                    }
                    
                    // Now recurse the scene and create the hierarchy
                    CreateHierarchy(volumes, voxelSize, loader.SceneGraph[0], root, Vector3.zero, Vector3.zero);
                    
                    // Now we can remove the initially loaded models as they have been duplicated into the hierarchy
                    foreach (var v in volumes)
                        GameObject.DestroyImmediate(v.gameObject);
                    
                    break;
                case 1:
                    // Animation or single model
                    if (original == null)
                    {
                        newVolume = (Editor.Instantiate(EditorUtility.VoxelVolumePrefab, Vector3.zero, Quaternion.identity) as GameObject).GetComponent<Volume>();
                        newVolume.name = importName != "MagicaVoxel Import" ? importName : Path.GetFileNameWithoutExtension(stream.Name);
                        newVolume.gameObject.AddComponent<MagicaVoxelImport>();
                        newVolume.GetComponent<MagicaVoxelImport>().ImportedFile = stream.Name;
                        newVolume.GetComponent<MagicaVoxelImport>().ImportedVoxelSize = voxelSize;
                        newVolume.GetComponent<MagicaVoxelImport>().ImportedMode = mode;
                        newVolume.GetComponent<Volume>().Material = EditorUtility.PicaVoxelDiffuseMaterial;
                        newVolume.GetComponent<Volume>().GenerateBasic(FillMode.None);
                    }
                    else newVolume = original.GetComponent<Volume>();
                    newVolume.XSize = loader.Largest.x;
                    newVolume.YSize = loader.Largest.y;
                    newVolume.ZSize = loader.Largest.z;
                    newVolume.Frames[newVolume.CurrentFrame].XSize = loader.Largest.x;
                    newVolume.Frames[newVolume.CurrentFrame].YSize = loader.Largest.y;
                    newVolume.Frames[newVolume.CurrentFrame].ZSize = loader.Largest.z;
                    newVolume.Frames[newVolume.CurrentFrame].Voxels = new Voxel[loader.Largest.x * loader.Largest.y * loader.Largest.z];
                    newVolume.VoxelSize = voxelSize;

                    if (newVolume.NumFrames != loader.Models.Count)
                    {
                        for (var i = newVolume.Frames.Count - 1; i > 0; i--)
                        {
                            newVolume.SetFrame(i);
                            newVolume.DeleteFrame();
                        }

                        for (var i = 0; i < loader.Models.Count - 1; i++)
                            newVolume.AddFrame(newVolume.NumFrames);
                    }

                    foreach(var f in newVolume.Frames)
                        for (int i = 0; i < f.Voxels.Length; i++) f.Voxels[i] = new Voxel() { Color = new Color32(0,0,0,255), State = VoxelState.Inactive, Value = 128 };

                    for (var m = 0;m<loader.Models.Count;m++)
                    {
                        for(var x=0;x<loader.Models[m].Size.x;x++)
                        for(var y=0;y<loader.Models[m].Size.y;y++)
                        for(var z=0;z<loader.Models[m].Size.z;z++)
                            if(loader.Models[m].Data[x,y,z]!=0) newVolume.Frames[m].Voxels[x + newVolume.XSize*(y + newVolume.YSize*z)] = new Voxel() {Color = loader.Colors[loader.Models[m].Data[x,y,z]], State = VoxelState.Active, Value = 128};
                    }
                    
                    newVolume.SetFrame(0);
                    newVolume.CreateChunks();
                    newVolume.SaveForSerialize();
                    break;
            }

        }

        private static void CreateHierarchy(List<Volume> volumes, float voxelSize, MagicaLoader.GraphNode graphNode, Transform parent, Vector3 translate, Vector3 rotate)
        {
            switch (graphNode.Type)
            {
                case MagicaLoader.GraphNodeType.Transform:
                    // set translate/rotate for future nodes using graphNode transform
                    byte[] trans = null;
                    byte[] rot = null;
                    if(graphNode.AttributesArray!=null)
                        foreach (var d in graphNode.AttributesArray)
                        {
                            if (d.ContainsKey("_t"))
                                trans = d["_t"];
                            if (d.ContainsKey("_r"))
                                rot = d["_r"];
                        }

                    if (trans != null)
                    {
                        var tS = Encoding.UTF8.GetString(trans).Split(' ');
                        translate = new Vector3(Convert.ToInt32(tS[0]),Convert.ToInt32(tS[2]),Convert.ToInt32(tS[1])) * voxelSize;
                    }
                    if (rot != null)
                    {
                        var t = GenericsReader.ReadRotation(rot[0]);
//                        var r = new BitArray(rot);
//                        byte test1 = 0;
//                        if (r[0])
//                            test1 = (byte) (test1 | (1 << 0));
//                        if(r[1])
//                            test1 = (byte) (test1 | (1 << 1));
//                        int p1 = Convert.ToInt16(test1);
//                        
//                        test1 = 0;
//                        if (r[2])
//                            test1 = (byte) (test1 | (1 << 0));
//                        if(r[3])
//                            test1 = (byte) (test1 | (1 << 1));
//                        int p2 = Convert.ToInt16(test1);
                    }
                    break;
                case MagicaLoader.GraphNodeType.Group:
                    // create a new group node and set parent/transform
                    var group = new GameObject();
                    group.name = "Group";
                    group.transform.SetParent(parent, false);
                    group.transform.localPosition = translate;
                    parent = group.transform;
                    break;
                case MagicaLoader.GraphNodeType.Shape:
                    // set the parent of the shape and transform
                    foreach (var s in graphNode.ModelIds)
                    {
                        var newVol = GameObject.Instantiate(volumes[s]);
                        newVol.name = newVol.name.Replace("(Clone)", "").TrimEnd(' ');
                        newVol.transform.SetParent(parent, false);
                        newVol.transform.localPosition = translate;
                    }

                    break;
            }
            
            foreach(var cn in graphNode.Children)
                CreateHierarchy(volumes, voxelSize, cn, parent, translate, rotate);
        }

        private static Volume CreateVolume(MagicaLoader.Model model, int index, Color32[] loaderColors, float voxelSize)
        {
            var newVolume = (Editor.Instantiate(EditorUtility.VoxelVolumePrefab, Vector3.zero, Quaternion.identity) as GameObject).GetComponent<Volume>();
            newVolume.name = "Model " + index;
            newVolume.GetComponent<Volume>().Material = EditorUtility.PicaVoxelDiffuseMaterial;
            newVolume.GetComponent<Volume>().GenerateBasic(FillMode.None);
            newVolume.XSize = model.Size.x;
            newVolume.YSize = model.Size.y;
            newVolume.ZSize = model.Size.z;
            newVolume.Frames[newVolume.CurrentFrame].XSize = model.Size.x;
            newVolume.Frames[newVolume.CurrentFrame].YSize = model.Size.y;
            newVolume.Frames[newVolume.CurrentFrame].ZSize = model.Size.z;
            newVolume.Frames[newVolume.CurrentFrame].Voxels = new Voxel[model.Size.x * model.Size.y * model.Size.z];
            newVolume.VoxelSize = voxelSize;

            for (int i = 0; i < newVolume.Frames[newVolume.CurrentFrame].Voxels.Length; i++)  newVolume.Frames[newVolume.CurrentFrame].Voxels[i] = new Voxel() { Color = new Color32(0,0,0,255), State = VoxelState.Inactive, Value = 128 };

            
            for(var x=0;x<model.Size.x;x++)
            for(var y=0;y<model.Size.y;y++)
            for(var z=0;z<model.Size.z;z++)
                if(model.Data[x,y,z]!=0) newVolume.Frames[newVolume.CurrentFrame].Voxels[x + newVolume.XSize*(y + newVolume.YSize*z)] = new Voxel() {Color = loaderColors[model.Data[x,y,z]], State = VoxelState.Active, Value = 128};
        
            
            newVolume.SetFrame(0);
            newVolume.CreateChunks();
            newVolume.SaveForSerialize();
            
            newVolume.Pivot = (new Vector3(newVolume.XSize, newVolume.YSize, newVolume.ZSize) * (voxelSize*0.5f));
            newVolume.UpdatePivot();

            return newVolume;
        }

        public static void MagicaVoxelImport(string fn, string volumeName, float voxelSize, int mode)
        {
//            var newObject = Editor.Instantiate(EditorUtility.VoxelVolumePrefab, Vector3.zero, Quaternion.identity) as GameObject;
//
//            newObject.name = (volumeName != "MagicaVoxel Import" ? volumeName : Path.GetFileNameWithoutExtension(fn));
//            newObject.GetComponent<Volume>().Material = EditorUtility.PicaVoxelDiffuseMaterial;
//            newObject.GetComponent<Volume>().GenerateBasic(FillMode.None);

            //using (BinaryReader stream = new BinaryReader(new FileStream(fn, FileMode.Open)))
            //{
                FromMagica(new FileStream(fn, FileMode.Open), volumeName, null, voxelSize, mode);
            //}

          //  newObject.GetComponent<Volume>().ImportedFile = fn;
          //  newObject.GetComponent<Volume>().ImportedFrom = Importer.Magica;
        }

        public static void MagicaVoxelImport(MagicaVoxelImport existingImport)
        {
            //using (BinaryReader stream = new BinaryReader(new FileStream(existingVolume.ImportedFile, FileMode.Open)))
            //{
                FromMagica(new FileStream(existingImport.ImportedFile, FileMode.Open), existingImport.gameObject.name, existingImport.gameObject, existingImport.ImportedVoxelSize, existingImport.ImportedMode);
            //}
        }

    }
}
