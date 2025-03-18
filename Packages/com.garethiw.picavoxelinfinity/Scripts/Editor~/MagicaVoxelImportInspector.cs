/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
// 
// Source code distributed under standard Asset Store licence:
// http://unity3d.com/legal/as_terms
//
/////////////////////////////////////////////////////////////////////////
using UnityEngine;
using UnityEditor;

namespace PicaVoxel
{
    [CustomEditor(typeof (MagicaVoxelImport))]
    public class MagicaVoxelImportInspector : Editor
    {
        private MagicaVoxelImport magicaImport;

        private void OnEnable()
        {
            magicaImport = (MagicaVoxelImport) target;
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space();
            if (!string.IsNullOrEmpty(magicaImport.ImportedFile) && serializedObject.targetObjects.Length < 2)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button(new GUIContent("Re-import from MagicaVoxel", "Re-import from original .VOX file")))
                {
                    if (UnityEditor.EditorUtility.DisplayDialog("Warning!",
                        "Re-importing will overwrite any changes made since original import, including hierarchy additions and components (for world imports). This cannot be undone!",
                        "OK", "Cancel"))
                    {
                        
                        MagicaVoxelImporter.MagicaVoxelImport(magicaImport);
                    }
                }
            }

        }
    }
}