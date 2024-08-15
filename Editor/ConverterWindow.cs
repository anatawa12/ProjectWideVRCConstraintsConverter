using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Anatawa12.VRCConstraintsConverter
{
    public class ConverterWindow : EditorWindow
    {
        [MenuItem("Tools/Project Wide VRC Constraints Converter")]
        public static void ShowWindow() => GetWindow<ConverterWindow>("VRC Constraints Converter");

        Vector2 scrollPosition;
        string[] prefabsAndScenes;
        TimeSpan lastSearchTime;

        private void OnGUI()
        {
            // TODO
            if (GUILayout.Button("Search for files"))
            {
                OnSearchFiles();
            }

            EditorGUILayout.LabelField($"Last search time: {lastSearchTime}");
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            if (prefabsAndScenes != null)
            {
                foreach (var file in prefabsAndScenes)
                {
                    // TODO: show in a better way (like tree view)
                    EditorGUILayout.LabelField(file);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        void OnSearchFiles()
        {
            var stopwatch = Stopwatch.StartNew();
            prefabsAndScenes = FindAssetsForConversion();
            stopwatch.Stop();
            lastSearchTime = stopwatch.Elapsed;
        }

        #region Find Assets For Conversion

        private static string[] FindAssetsForConversion()
        {
            EditorUtility.DisplayProgressBar("Gathering files", "Finding files to convert", 0);

            var allAssetGUIDs = AssetDatabase.FindAssets("t:object");
            var filesToConvert = new List<string>();

            try
            {
                for (var index = 0; index < allAssetGUIDs.Length; index++)
                {
                    var assetGuid = allAssetGUIDs[index];
                    var assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);

                    var progress = (float)index / allAssetGUIDs.Length;
                    EditorUtility.DisplayProgressBar("Gathering files", $"Processing {assetPath}", progress);

                    var extension = Path.GetExtension(assetPath);

                    switch (extension)
                    {
                        case ".unity":
                            // scene files are hard to determine if they contain constraints
                            // so we just add them to the list
                            filesToConvert.Add(assetPath);
                            break;
                        case ".prefab":
                            // for prefab assets, we check if the prefab contains constraints
                            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath)
                                .GetComponentsInChildren<IConstraint>().Any())
                            {
                                filesToConvert.Add(assetPath);
                            }

                            break;

                        case ".asset":
                        case ".controller":
                        case ".mesh":
                            // for asset files, we check if the asset contains animation clips animating constraints
                            if (AssetDatabase.LoadAllAssetsAtPath(assetPath).Any(ShouldConvertAssetFile))
                                filesToConvert.Add(assetPath);
                            break;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return filesToConvert.ToArray();
        }

        private static bool ShouldConvertAssetFile(Object o)
        {
            // the Constraint component is a interface of Unity Constraints
            if (o is AnimationClip clip)
            {
                // TODO: test if the animation clip contains animation about constraints
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    // Constraints are extending Behavior, and animating Behavior would affect VRCConstraints automatically
                    // so we can ignore them
                    if (binding.type == typeof(AimConstraint) || binding.type == typeof(LookAtConstraint) ||
                        binding.type == typeof(ParentConstraint) || binding.type == typeof(PositionConstraint) ||
                        binding.type == typeof(RotationConstraint) || binding.type == typeof(ScaleConstraint))
                    {
                        // the animation clip contains animation about constraints so we should convert it
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}