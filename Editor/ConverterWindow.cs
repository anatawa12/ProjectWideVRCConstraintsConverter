#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using TreeView = UnityEditor.IMGUI.Controls.TreeView;

namespace Anatawa12.VRCConstraintsConverter
{
    public class ConverterWindow : EditorWindow
    {
        [MenuItem("Tools/Project Wide VRC Constraints Converter")]
        public static void ShowWindow() => GetWindow<ConverterWindow>("VRC Constraints Converter");

        Vector2 scrollPosition;
        Vector2 errorScrollPosition;
        FindResult[] assetsToConvert = Array.Empty<FindResult>();
        [SerializeField] TreeViewState assetsTreeViewState = null!;
        AssetsTreeView? assetsTreeView;
        List<ErrorForObject> errors = new();

        bool removeUnityConstraintProperties = true;
        bool convertScenes = true;
        bool convertPrefabs = true;
        bool convertAnimationClips = true;

        private void OnEnable()
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (assetsTreeViewState == null)
                assetsTreeViewState = new TreeViewState();
            assetsTreeView = new AssetsTreeView(assetsTreeViewState);
        }

        private FindResult[] ActiveResults()
        {
            if (assetsToConvert != assetsTreeView?.Assets)
                throw new InvalidOperationException("assetsToConvert != assetsTreeView.Assets");
            return assetsTreeView.GetEnabledAssets();
        }

        private void OnGUI()
        {
            ControlArea();

            EditorGUILayout.LabelField("Assets to be proceed:");
            EditorGUILayout.HelpBox(
                "It's hard to determine if an scene is containing constraints before, so all scenes will be processed.",
                MessageType.Info
            );
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUI.indentLevel++;
            AssetList();
            EditorGUI.indentLevel--;
            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField("Errors from previous process:");
            errorScrollPosition = EditorGUILayout.BeginScrollView(errorScrollPosition);
            EditorGUI.indentLevel++;
            ErrorList();
            EditorGUI.indentLevel--;
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
        }

        void ControlArea()
        {
            if (GUILayout.Button("Search files to convert"))
            {
                assetsToConvert = FindAssetsForConversion();
            }

            GUILayout.Label("Options:");

            convertScenes = EditorGUILayout.ToggleLeft("Convert Scenes", convertScenes);
            convertPrefabs = EditorGUILayout.ToggleLeft("Convert Prefabs", convertPrefabs);
            convertAnimationClips = EditorGUILayout.ToggleLeft("Convert Animation Clips", convertAnimationClips);

            EditorGUI.BeginDisabledGroup(!convertAnimationClips);
            EditorGUI.indentLevel++;
            removeUnityConstraintProperties = EditorGUILayout.ToggleLeft(
                "Remove Unity Constraint Properties from Animation Clips", removeUnityConstraintProperties);
            EditorGUI.indentLevel--;
            EditorGUI.EndDisabledGroup();

            using (new EditorGUI.DisabledScope(assetsToConvert.Length == 0))
            {
                if (GUILayout.Button("Convert"))
                {
                    if (AskForBackup())
                    {
                        errors.Clear();

                        using var sceneRestore = convertScenes ? new RestoreOpeningScenes() : null;

                        Undo.IncrementCurrentGroup();
                        var group = Undo.GetCurrentGroup();
                        var assets = ActiveResults();

                        if (!convertScenes) assets = assets.Where(x => x.Type != AsseetType.Scene).ToArray();
                        if (!convertPrefabs) assets = assets.Where(x => x.Type != AsseetType.Prefab).ToArray();
                        if (!convertAnimationClips) assets = assets.Where(x => x.Type != AsseetType.Asset).ToArray();

                        // we have two phase for prefab conversion so twice count for prefab
                        var total = assets.Length + assets.Count(x => x.Type == AsseetType.Prefab);

                        using var callback = new SimpleProgressCallback("Converting Assets", total);

                        callback.Title = "Converting Assets (Prefab Phase 1)";
                        ConvertPrefabPhase1(assets, callback.OnProgress);
                        callback.Title = "Converting Assets (Scenes)";
                        ConvertScenes(assets, callback.OnProgress);
                        callback.Title = "Converting Assets (Prefab Phase 2)";
                        ConvertPrefabPhase2(assets, callback.OnProgress);
                        callback.Title = "Converting Assets (Animation Clips)";
                        ConvertAnimationClips(assets, callback.OnProgress);

                        Undo.CollapseUndoOperations(group);
                        Undo.SetCurrentGroupName("Convert Unity Constraints to VRC Constraints");
                    }
                }
            }

            DebugArea();
        }

        void AssetList()
        {
            if (assetsTreeView!.Assets != assetsToConvert)
            {
                assetsTreeView.Assets = assetsToConvert;
                assetsTreeView.Reload();
            }

            if (assetsToConvert.Length != 0)
            {
                var height = EditorGUILayout.GetControlRect(false, assetsTreeView.totalHeight);
                assetsTreeView.OnGUI(height);
            }
            else
            {
                EditorGUILayout.LabelField("Nothing to convert");
            }
        }

        void ErrorList()
        {
            if (errors.Count != 0)
            {
                foreach (var error in errors)
                {
                    EditorGUILayout.LabelField(error.obj.name + ": " + error.error);
                }
            }
            else
            {
                EditorGUILayout.LabelField("No errors at this time");
            }
        }

        #region Debug Area

        private bool openDebugMenu;
        private AnimationClip? clip;
        private GameObject? gameObject;
        private Behaviour? constraint;

        void DebugArea()
        {
            openDebugMenu = EditorGUILayout.Foldout(openDebugMenu, "Debug");
            if (!openDebugMenu) return;

            clip = EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false) as AnimationClip;
            if (clip != null)
            {
                if (GUILayout.Button("Convert Animation Clip keeping Unity Constraints binding"))
                {
                    Undo.RecordObject(clip, "Convert Unity Constraints to VRC Constraints");
                    ConvertAnimationClip(clip, false);
                }

                if (GUILayout.Button("Convert Animation Clip"))
                {
                    Undo.RecordObject(clip,
                        "Convert Unity Constraints to VRC Constraints with removing old properties");
                    ConvertAnimationClip(clip, true);
                }
            }

            gameObject = EditorGUILayout.ObjectField("GameObject", gameObject, typeof(GameObject), true) as GameObject;
            if (gameObject != null)
            {
                if (GUILayout.Button("Convert Whole GameObject"))
                {
                    Undo.IncrementCurrentGroup();
                    var group = Undo.GetCurrentGroup();
                    ConvertGameObjectPhase1(gameObject);
                    ConvertGameObjectPhase2(gameObject);
                    Undo.CollapseUndoOperations(group);
                    Undo.SetCurrentGroupName("Convert Unity Constraints to VRC Constraints");
                }
            }

            constraint = EditorGUILayout.ObjectField("Constraint", constraint, typeof(IConstraint), true) as Behaviour;
            if (constraint && constraint is IConstraint iconstraint)
            {
                if (GUILayout.Button("Convert Constraint"))
                {
                    Undo.RecordObject(constraint, "Convert Unity Constraints to VRC Constraints");
                    ConvertConstraintPhase1(iconstraint);
                }
            }
        }

        #endregion

        bool AskForBackup()
        {
            return EditorUtility.DisplayDialog("Backup your project!",
                "Please backup your project before converting constraints.\n" +
                "This tool will modify your project files and it may cause data loss.\n" +
                "Do you want to continue?", "Yes Convert!", "No, I'll backup");
        }

        void ConvertAnimationClips(FindResult[] assets, Action<string>? onProgress = null)
        {
            foreach (var asset in assets)
            {
                if (asset.Type != AsseetType.Asset) continue;
                onProgress?.Invoke(asset.Path);
                if (!asset.IsConvertible) continue;
                foreach (var obj in asset.Objects)
                {
                    if (obj is AnimationClip animationClip)
                    {
                        Undo.RecordObject(animationClip, "Convert Unity Constraints to VRC Constraints");
                        ConvertAnimationClip(animationClip, removeUnityConstraintProperties);
                    }
                }
            }
        }

        void ConvertPrefabPhase1(FindResult[] assets, Action<string>? onProgress = null)
        {
            var prefabAssets = assets.Where(x => x.Type == AsseetType.Prefab).ToArray();
            var resultByPrefab = prefabAssets.ToDictionary(x => x.GameObject, x => x);
            var sorted = Utility.SortPrefabsParentToChild(prefabAssets.Select(x => x.GameObject));

            foreach (var prefabAsset in sorted)
            {
                var result = resultByPrefab[prefabAsset];
                onProgress?.Invoke(result.Path);
                if (!result.IsConvertible) continue;
                var changed = ConvertGameObjectPhase1(prefabAsset);
                if (changed)
                    PrefabUtility.SavePrefabAsset(prefabAsset);
            }
        }

        void ConvertPrefabPhase2(FindResult[] assets, Action<string>? onProgress = null)
        {
            // in phase 2, we only remove the old constraints with checking if it's prefab instance
            // so we don't need to sort the prefabs
            var prefabAssets = assets.Where(x => x.Type == AsseetType.Prefab).ToArray();

            foreach (var result in prefabAssets)
            {
                onProgress?.Invoke(result.Path);
                if (!result.IsConvertible) continue;
                var changed = ConvertGameObjectPhase2(result.GameObject);
                if (changed)
                    PrefabUtility.SavePrefabAsset(result.GameObject);
            }
        }

        void ConvertScenes(FindResult[] assets, Action<string>? onProgress = null)
        {
            foreach (var asset in assets)
            {
                if (asset.Type != AsseetType.Scene) continue;
                onProgress?.Invoke(asset.Path);
                if (!asset.IsConvertible) continue;
                var scene = EditorSceneManager.OpenScene(asset.Path, OpenSceneMode.Single);

                var rootGameObjects = scene.GetRootGameObjects();
                var changed = false;
                foreach (var rootGameObject in rootGameObjects)
                {
                    // since they're not prefab, we can just convert them in one phase
                    changed |= ConvertGameObjectPhase1(rootGameObject);
                    changed |= ConvertGameObjectPhase2(rootGameObject);
                }

                if (changed)
                    EditorSceneManager.SaveScene(scene);
            }
        }

        #region Find Assets For Conversion

        class FindResult
        {
            public readonly string Path;
            public readonly AsseetType Type;
            public readonly Object[] Objects;

            private FindResult(string path, AsseetType type, Object[] objects)
            {
                Path = path;
                Type = type;
                Objects = objects;
            }

            public GameObject GameObject => (GameObject)Objects[0];

            public bool IsConvertible => !Utility.IsInReadOnlyPackage(Path);

            public static FindResult Scene(string assetPath) => new(assetPath, AsseetType.Scene, Array.Empty<Object>());

            public static FindResult Prefab(string assetPath, GameObject prefab) =>
                new(assetPath, AsseetType.Prefab, new[] { prefab });

            public static FindResult Asset(string assetPath, Object[] assetFiles) =>
                new(assetPath, AsseetType.Asset, assetFiles);
        }

        enum AsseetType
        {
            Scene,
            Prefab,
            Asset,
        }

        private static FindResult[] FindAssetsForConversion()
        {
            EditorUtility.DisplayProgressBar("Gathering files", "Finding files to convert", 0);

            var allAssetGUIDs = AssetDatabase.FindAssets("t:object");
            var filesToConvert = new List<FindResult>();

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
                            filesToConvert.Add(FindResult.Scene(assetPath));
                            break;
                        case ".prefab":
                            // for prefab assets, we check if the prefab contains constraints
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                            if (prefab != null && prefab.GetComponentsInChildren<IConstraint>().Any())
                            {
                                filesToConvert.Add(FindResult.Prefab(assetPath, prefab));
                            }

                            break;

                        case ".asset":
                        case ".controller":
                        case ".mesh":
                        case ".anim":
                            // for asset files, we check if the asset contains animation clips animating constraints
                            var assetFiles = AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(ShouldConvertAssetFile)
                                .ToArray();
                            if (assetFiles.Length > 0)
                                filesToConvert.Add(FindResult.Asset(assetPath, assetFiles));
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
                foreach (var binding in AnimationUtility.GetCurveBindings(clip)
                             .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    // Constraints are extending Behavior, and animating Behavior would affect VRCConstraints automatically
                    // so we can ignore them
                    if (ConstraintTypeMapping.ContainsKey(binding.type))
                    {
                        // the animation clip contains animation about constraints so we should convert it
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Convert Animation Clip

        private static readonly Dictionary<Type, Type> ConstraintTypeMapping = new()
        {
            { typeof(AimConstraint), typeof(VRCAimConstraint) },
            { typeof(LookAtConstraint), typeof(VRCLookAtConstraint) },
            { typeof(ParentConstraint), typeof(VRCParentConstraint) },
            { typeof(PositionConstraint), typeof(VRCPositionConstraint) },
            { typeof(RotationConstraint), typeof(VRCRotationConstraint) },
            { typeof(ScaleConstraint), typeof(VRCScaleConstraint) },
        };

        private void ConvertAnimationClip(AnimationClip clip, bool removeOld)
        {
            var unsupportedProperties = new List<string>();

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (ConstraintTypeMapping.TryGetValue(binding.type, out var newType))
                {
                    var newBinding = binding;
                    newBinding.type = newType;

                    if (TryMapConstraintProperty(binding.propertyName, out var newPropertyName, out var supported))
                    {
                        newBinding.propertyName = newPropertyName;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (removeOld) AnimationUtility.SetEditorCurve(clip, binding, null);
                        AnimationUtility.SetEditorCurve(clip, newBinding, curve);

                        // some properties can be mapped but not supported
                        if (!supported) unsupportedProperties.Add(binding.propertyName);
                    }
                    else
                    {
                        unsupportedProperties.Add(binding.propertyName);
                    }
                }
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (ConstraintTypeMapping.TryGetValue(binding.type, out var newType))
                {
                    var newBinding = binding;
                    newBinding.type = newType;

                    if (TryMapConstraintProperty(binding.propertyName, out var newPropertyName, out var supported))
                    {
                        newBinding.propertyName = newPropertyName;
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (removeOld) AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                        // TODO: this mapping may not work for transform pptr mapping.
                        // We may need to use a different method to map pptr properties
                        AnimationUtility.SetObjectReferenceCurve(clip, newBinding, curve);

                        // some properties can be mapped but not supported
                        if (!supported) unsupportedProperties.Add(binding.propertyName);
                    }
                    else
                    {
                        unsupportedProperties.Add(binding.propertyName);
                    }
                }
            }

            if (unsupportedProperties.Count > 0)
            {
                // TODO: show error (or warning)
                Debug.LogWarning($"Unsupported properties: {string.Join(", ", unsupportedProperties)}", clip);
                errors ??= new List<ErrorForObject>();
                errors.Add(new ErrorForObject(clip, $"Unsupported properties: {string.Join(", ", unsupportedProperties)}"));;
            }
        }

        // one to one property mapping
        private static readonly Dictionary<string, string> SimplePropertyMapping = new()
        {
            // behavior
            { "m_Enabled", "m_Enabled" },
            // constraint
            // aim
            { "m_Active", "IsActive" },
            { "m_Weight", "GlobalWeight" },
            { "m_RotationAtRest.x", "RotationAtRest.x" },
            { "m_RotationAtRest.y", "RotationAtRest.y" },
            { "m_RotationAtRest.z", "RotationAtRest.z" },
            { "m_RotationOffset.x", "RotationOffset.x" },
            { "m_RotationOffset.y", "RotationOffset.y" },
            { "m_RotationOffset.z", "RotationOffset.z" },
            { "m_AffectRotationX", "AffectsRotationX" },
            { "m_AffectRotationY", "AffectsRotationY" },
            { "m_AffectRotationZ", "AffectsRotationZ" },
            { "m_WorldUpObject", "WorldUpTransform" },
            { "m_AimVector.x", "AimAxis.x" },
            { "m_AimVector.y", "AimAxis.y" },
            { "m_AimVector.z", "AimAxis.z" },
            { "m_UpVector.x", "UpAxis.x" },
            { "m_UpVector.y", "UpAxis.y" },
            { "m_UpVector.z", "UpAxis.z" },
            { "m_WorldUpVector.x", "WorldUpVector.x" },
            { "m_WorldUpVector.y", "WorldUpVector.y" },
            { "m_WorldUpVector.z", "WorldUpVector.z" },
            // LookAt
            { "m_Roll", "Roll" },
            { "m_UseUpObject", "UseUpTransform" },

            // parent
            { "m_AffectTranslationX", "AffectsPositionX" },
            { "m_AffectTranslationY", "AffectsPositionY" },
            { "m_AffectTranslationZ", "AffectsPositionZ" },
            { "m_TranslationAtRest.x", "PositionAtRest.x" },
            { "m_TranslationAtRest.y", "PositionAtRest.y" },
            { "m_TranslationAtRest.z", "PositionAtRest.z" },

            // position
            { "m_TranslationOffset.x", "PositionOffset.x" },
            { "m_TranslationOffset.y", "PositionOffset.y" },
            { "m_TranslationOffset.z", "PositionOffset.z" },

            // no rotation specific (shared with aim / lookat)

            // scale
            { "m_AffectScalingX", "AffectsScaleX" },
            { "m_AffectScalingY", "AffectsScaleY" },
            { "m_AffectScalingZ", "AffectsScaleZ" },
            { "m_ScaleAtRest.x", "ScaleAtRest.x" },
            { "m_ScaleAtRest.y", "ScaleAtRest.y" },
            { "m_ScaleAtRest.z", "ScaleAtRest.z" },
            { "m_ScaleOffset.x", "ScaleOffset.x" },
            { "m_ScaleOffset.y", "ScaleOffset.y" },
            { "m_ScaleOffset.z", "ScaleOffset.z" },
        };

        // m_Sources.Array.data[0].sourceTransform
        // mapped to `SourceTransform` but it's not supported officially by Unity (PPtr inside struct)
        private static readonly Regex SourceTransform = new(@"^m_Sources\.Array\.data\[(\d+)\]\.sourceTransform$");

        // m_Sources.Array.data[0].weight
        private static readonly Regex SourceWeight = new(@"^m_Sources\.Array\.data\[(\d+)\]\.weight$");

        // m_TranslationOffsets.Array.data[0].x
        private static readonly Regex TranslationOffsetX = new(@"^m_TranslationOffsets\.Array\.data\[(\d+)\]\.x$");

        // m_TranslationOffsets.Array.data[0].y
        private static readonly Regex TranslationOffsetY = new(@"^m_TranslationOffsets\.Array\.data\[(\d+)\]\.y$");

        // m_TranslationOffsets.Array.data[0].z
        private static readonly Regex TranslationOffsetZ = new(@"^m_TranslationOffsets\.Array\.data\[(\d+)\]\.z$");

        // m_RotationOffsets.Array.data[0].x
        private static readonly Regex RotationOffsetX = new(@"^m_RotationOffsets\.Array\.data\[(\d+)\]\.x$");

        // m_RotationOffsets.Array.data[0].y
        private static readonly Regex RotationOffsetY = new(@"^m_RotationOffsets\.Array\.data\[(\d+)\]\.y$");

        // m_RotationOffsets.Array.data[0].z
        private static readonly Regex RotationOffsetZ = new(@"^m_RotationOffsets\.Array\.data\[(\d+)\]\.z$");

        private static readonly (Regex, string)[] MatchSourcePropertyNameMapping = new[]
        {
            (SourceWeight, "Weight"),
            (TranslationOffsetX, "ParentPositionOffset.x"),
            (TranslationOffsetY, "ParentPositionOffset.y"),
            (TranslationOffsetZ, "ParentPositionOffset.z"),
            (RotationOffsetX, "ParentRotationOffset.x"),
            (RotationOffsetY, "ParentRotationOffset.y"),
            (RotationOffsetZ, "ParentRotationOffset.z"),
        };

        /*
           Those properties are properties on the VRCConstraints but not on Unity Constraints.
           If we implement back conversion, we need to make error (or warning) for those properties.

           TargetTransform(pptr)
           SolveInLocalSpace
           FreezeToWorld
           RebakeOffsetsWhenUnfrozen
           Locked
         */

        static bool TryMapConstraintProperty(string propertyName, out string? newPropertyName, out bool supported)
        {
            if (SimplePropertyMapping.TryGetValue(propertyName, out newPropertyName))
            {
                supported = true;
                return true;
            }

            // non simple case: sources and config related to each sources
            int index;

            if (MatchArrayData(propertyName, SourceTransform, out index))
            {
                newPropertyName = $"Sources.source{index}.SourceTransform";
                Debug.LogWarning($"PPtr mapping is not supported: {propertyName}");
                supported = false; // PPtr is not supported officially
                return true;
            }

            foreach (var (regex, property) in MatchSourcePropertyNameMapping)
            {
                if (MatchArrayData(propertyName, regex, out index))
                {
                    newPropertyName = $"Sources.source{index}.{property}";
                    supported = true;
                    return true;
                }
            }

            newPropertyName = null;
            supported = false;
            return false;

            bool MatchArrayData(string propertyName, Regex regex, out int index)
            {
                var match = regex.Match(propertyName);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, out index) &&
                    index >= 0 &&
                    index < VRCConstraintSourceKeyableList.MaxFlatLength)
                {
                    return true;
                }

                index = -1;
                return false;
            }
        }

        #endregion

        #region Convert Constraint Component

        static bool ConvertGameObjectPhase1(GameObject gameObject)
        {
            Undo.SetCurrentGroupName("Convert Unity Constraints to VRC Constraints");
            var constraints = gameObject.GetComponentsInChildren<IConstraint>();
            var modified = false;
            foreach (var constraint in constraints)
                modified |= ConvertConstraintPhase1(constraint);
            return modified;
        }

        static bool ConvertGameObjectPhase2(GameObject gameObject)
        {
            Undo.SetCurrentGroupName("Convert Unity Constraints to VRC Constraints");
            var constraints = gameObject.GetComponentsInChildren<IConstraint>();
            foreach (var constraint in constraints)
            {
                if (!PrefabUtility.IsPartOfPrefabInstance((Component)constraint))
                    Undo.DestroyObjectImmediate((Behaviour)constraint);
            }
            return constraints.Length > 0;
        }

        private static bool ConvertConstraintPhase1(IConstraint constraint)
        {
            if (PrefabUtility.IsPartOfPrefabInstance((Component)constraint))
            {
                return ConvertConstraintPrefabInstance(constraint);
            }
            else
            {
                ConvertConstraintNew(constraint);
                return true;
            }
        }

        private static void ConvertConstraintNew(IConstraint constraint)
        {
            var oldConstraint = (Behaviour)constraint;

            if (!ConstraintTypeMapping.TryGetValue(constraint.GetType(), out var newType))
            {
                Debug.LogError("Unsupported constraint type: " + oldConstraint.GetType(), oldConstraint);
                return;
            }

            var targetGameObject = oldConstraint.gameObject;

            // add component to position
            var newComponent = (VRCConstraintBase)Undo.AddComponent(targetGameObject, newType);
            MoveComponentRelativeToComponent(newComponent, oldConstraint, true);
            Undo.RecordObject(newComponent, "Convert Unity Constraints to VRC Constraints");

            CopyConstraintProperties(constraint, newComponent);

            // we won't remove the old component here
            // because we need to keep for prefab instance overrides
        }

        private static bool ConvertConstraintPrefabInstance(IConstraint constraint)
        {
            var asBehavior = (Behaviour)constraint;
            if (!ConstraintTypeMapping.TryGetValue(constraint.GetType(), out var newType))
            {
                Debug.LogError("Unsupported constraint type: " + constraint.GetType(), asBehavior);
                return false;
            }

            // it's prefab instance, so just update the prefab
            var newComponent = asBehavior.GetComponents(newType).Cast<VRCConstraintBase>()
                .First(x => x.TargetTransform == null);

            var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(asBehavior);
            var changesCount = PrefabUtility.GetObjectOverrides(instanceRoot).Count;
            CopyConstraintProperties(constraint, newComponent);
            PrefabUtility.RecordPrefabInstancePropertyModifications(newComponent);
            var changesCountNew = PrefabUtility.GetObjectOverrides(instanceRoot).Count;
            return changesCountNew != changesCount;
        }

        private static void CopyConstraintProperties(IConstraint constraint, VRCConstraintBase newComponent)
        {
            var oldConstraint = (Behaviour)constraint;

            // copy properties
            // first, Common IConstraint properties
            newComponent.enabled = oldConstraint.enabled;
            newComponent.IsActive = constraint.constraintActive;
            newComponent.GlobalWeight = constraint.weight;
            newComponent.Locked = constraint.locked;
            newComponent.Sources.SetLength(constraint.sourceCount);
            for (var i = 0; i < constraint.sourceCount; i++)
            {
                var source = constraint.GetSource(i);
                // We have to use new VRCConstraintSource.
                // if we won't, the source will be replaced with default values on serialization.
                newComponent.Sources[i] = new VRCConstraintSource(source.sourceTransform, source.weight,
                    Vector3.zero, Vector3.zero);
            }

            // then, type specific properties
            switch (constraint)
            {
                case AimConstraint aim:
                    var newAim = (VRCAimConstraint)newComponent;
                    newAim.RotationAtRest = aim.rotationAtRest;
                    newAim.RotationOffset = aim.rotationOffset;
                    newAim.AffectsRotationX = (aim.rotationAxis & Axis.X) != 0;
                    newAim.AffectsRotationY = (aim.rotationAxis & Axis.Y) != 0;
                    newAim.AffectsRotationZ = (aim.rotationAxis & Axis.Z) != 0;
                    newAim.AimAxis = aim.aimVector;
                    newAim.UpAxis = aim.upVector;
                    newAim.WorldUpVector = aim.worldUpVector;
                    newAim.WorldUpTransform = aim.worldUpObject;
                    newAim.WorldUp = ToVRChat(aim.worldUpType);
                    break;
                case LookAtConstraint lookAt:
                    var newLookAt = (VRCLookAtConstraint)newComponent;
                    newLookAt.Roll = lookAt.roll;
                    newLookAt.RotationAtRest = lookAt.rotationAtRest;
                    newLookAt.RotationOffset = lookAt.rotationOffset;
                    newLookAt.WorldUpTransform = lookAt.worldUpObject;
                    newLookAt.UseUpTransform = lookAt.useUpObject;
                    break;
                case ParentConstraint parent:
                    var newParent = (VRCParentConstraint)newComponent;
                    newParent.PositionAtRest = parent.translationAtRest;
                    newParent.RotationAtRest = parent.rotationAtRest;
                    var positionOffsets = parent.translationOffsets;
                    for (var i = 0; i < Math.Min(newParent.Sources.Count, positionOffsets.Length); i++)
                    {
                        var source = newParent.Sources[i];
                        source.ParentPositionOffset = positionOffsets[i];
                        newParent.Sources[i] = source;
                    }

                    var rotationOffsets = parent.rotationOffsets;
                    for (var i = 0; i < Math.Min(newParent.Sources.Count, rotationOffsets.Length); i++)
                    {
                        var source = newParent.Sources[i];
                        source.ParentRotationOffset = rotationOffsets[i];
                        newParent.Sources[i] = source;
                    }

                    newParent.AffectsPositionX = (parent.translationAxis & Axis.X) != 0;
                    newParent.AffectsPositionY = (parent.translationAxis & Axis.Y) != 0;
                    newParent.AffectsPositionZ = (parent.translationAxis & Axis.Z) != 0;

                    newParent.AffectsRotationX = (parent.rotationAxis & Axis.X) != 0;
                    newParent.AffectsRotationY = (parent.rotationAxis & Axis.Y) != 0;
                    newParent.AffectsRotationZ = (parent.rotationAxis & Axis.Z) != 0;
                    break;
                case PositionConstraint position:
                    var newPosition = (VRCPositionConstraint)newComponent;
                    newPosition.PositionAtRest = position.translationAtRest;
                    newPosition.PositionOffset = position.translationOffset;
                    newPosition.AffectsPositionX = (position.translationAxis & Axis.X) != 0;
                    newPosition.AffectsPositionY = (position.translationAxis & Axis.Y) != 0;
                    newPosition.AffectsPositionZ = (position.translationAxis & Axis.Z) != 0;
                    break;
                case RotationConstraint rotation:
                    var newRotation = (VRCRotationConstraint)newComponent;
                    newRotation.RotationAtRest = rotation.rotationAtRest;
                    newRotation.RotationOffset = rotation.rotationOffset;
                    newRotation.AffectsRotationX = (rotation.rotationAxis & Axis.X) != 0;
                    newRotation.AffectsRotationY = (rotation.rotationAxis & Axis.Y) != 0;
                    newRotation.AffectsRotationZ = (rotation.rotationAxis & Axis.Z) != 0;
                    break;
                case ScaleConstraint scale:
                    var newScale = (VRCScaleConstraint)newComponent;
                    newScale.ScaleAtRest = scale.scaleAtRest;
                    newScale.ScaleOffset = scale.scaleOffset;
                    newScale.AffectsScaleX = (scale.scalingAxis & Axis.X) != 0;
                    newScale.AffectsScaleY = (scale.scalingAxis & Axis.Y) != 0;
                    newScale.AffectsScaleZ = (scale.scalingAxis & Axis.Z) != 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(constraint), constraint.GetType().Name,
                        "Unsupported constraint type");
            }
        }

        private static VRCConstraintBase.WorldUpType ToVRChat(AimConstraint.WorldUpType aimWorldUpType)
        {
            switch (aimWorldUpType)
            {
                case AimConstraint.WorldUpType.SceneUp:
                    return VRCConstraintBase.WorldUpType.SceneUp;
                case AimConstraint.WorldUpType.ObjectUp:
                    return VRCConstraintBase.WorldUpType.ObjectUp;
                case AimConstraint.WorldUpType.ObjectRotationUp:
                    return VRCConstraintBase.WorldUpType.ObjectRotationUp;
                case AimConstraint.WorldUpType.Vector:
                    return VRCConstraintBase.WorldUpType.Vector;
                default:
                    return VRCConstraintBase.WorldUpType.None;
            }
        }

        #endregion

        class ErrorForObject
        {
            public readonly Object obj;
            public readonly string error;

            public ErrorForObject(Object obj, string error)
            {
                this.obj = obj;
                this.error = error;
            }
        }

        #region Reflection Util

        private static readonly MethodInfo MoveComponentRelativeToComponentMethodInfo =
            typeof(UnityEditorInternal.ComponentUtility)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .First(e => e.Name == "MoveComponentRelativeToComponent" && e.GetParameters().Length == 3);

        internal static void MoveComponentRelativeToComponent(Component component, Component targetComponent,
            bool aboveTarget)
        {
            MoveComponentRelativeToComponentMethodInfo.Invoke(null,
                new object[] { component, targetComponent, aboveTarget });
        }

        #endregion

        #region Progress Support

        class SimpleProgressCallback : IDisposable
        {
            private readonly int _total;
            private int _current;

            public SimpleProgressCallback(string title, int total)
            {
                Title = title;
                _total = total;
            }

            public string Title { get; set; }

            public void Dispose()
            {
                EditorUtility.ClearProgressBar();
            }

            public void OnProgress(string obj)
            {
                _current++;
                EditorUtility.DisplayProgressBar(Title, obj, (float)_current / _total);
            }
        }

        class RestoreOpeningScenes : IDisposable
        {
            private readonly Scene _scene;
            private readonly string[]? _openingScenePaths;

            public RestoreOpeningScenes()
            {
                var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToArray();
                if (scenes.Any(x => x.isDirty))
                {
                    if (!EditorSceneManager.SaveScenes(scenes))
                    {
                        throw new InvalidOperationException("Failed to save scenes");
                    }
                }

                _openingScenePaths = scenes.Select(x => x.path).ToArray();
                if (_openingScenePaths.Any(string.IsNullOrEmpty))
                    _openingScenePaths = null;
            }

            public void Dispose()
            {
                if (_openingScenePaths != null)
                {
                    if (EditorUtility.DisplayDialog("Reopen?", "Do you want to reopen previously opened scenes?", "Yes",
                            "No"))
                    {
                        EditorSceneManager.OpenScene(_openingScenePaths[0]);
                        foreach (var openingScenePath in _openingScenePaths.Skip(1))
                            EditorSceneManager.OpenScene(openingScenePath, OpenSceneMode.Additive);
                    }
                }
            }
        }

        #endregion

        #region AssetsTreeView

        class AssetsTreeView : TreeView
        {
            public FindResult[] Assets = Array.Empty<FindResult>();

            private const int toggleWidth = 30;

            public AssetsTreeView(TreeViewState state) : base(state)
            {
                extraSpaceBeforeIconAndLabel = toggleWidth;
            }

            private IEnumerable<FindResult> FindAllEnabledItems()
            {
                var stack = new Stack<AssetTreeViewItem>();
                foreach (var item in rootItem.children.Cast<AssetTreeViewItem>())
                    stack.Push(item);
                while (stack.Count > 0)
                {
                    var assetItem = stack.Pop();
                    if (assetItem.readOnly || !assetItem.enabled)
                        continue;

                    if (assetItem.AssetInfo != null)
                        yield return assetItem.AssetInfo;

                    if (assetItem.children != null)
                        for (var index = assetItem.children.Count - 1; index >= 0; index--)
                            stack.Push((AssetTreeViewItem)assetItem.children[index]);
                }
            }

            public FindResult[] GetEnabledAssets() => FindAllEnabledItems().ToArray();

            protected override TreeViewItem BuildRoot()
            {
                var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };

                var id = 1;

                var directoryTreeItems = new Dictionary<string, AssetTreeViewItem>();

                AssetTreeViewItem GetItem(string path)
                {
                    if (directoryTreeItems.TryGetValue(path, out var item))
                        return item;

                    var slashIndex = path.LastIndexOf('/');
                    TreeViewItem parent;
                    string name;
                    if (slashIndex == -1)
                    {
                        parent = root;
                        name = path;
                    }
                    else
                    {
                        var parentPath = path.Substring(0, slashIndex);
                        parent = GetItem(parentPath);
                        name = path.Substring(slashIndex + 1);
                    }
                    var readOnly = Utility.IsInReadOnlyPackage(path);
                    item = new AssetTreeViewItem
                    {
                        id = id++,
                        displayName = name,
                        enabled = !readOnly,
                        readOnly = readOnly,
                    };
                    parent.AddChild(item);
                    directoryTreeItems[path] = item;
                    return item;
                }

                // ensure at least one root
                GetItem("Assets");

                foreach (var asset in Assets)
                    GetItem(asset.Path).AssetInfo = asset;

                // flatten tree if only one child
                void FlattenTree(AssetTreeViewItem item)
                {
                    if (item.children == null) return;
                    if (item.children.Count == 1)
                    {
                        var child = (AssetTreeViewItem) item.children[0];
                        item.displayName += "/" + child.displayName;
                        item.children = child.children;
                        item.AssetInfo = child.AssetInfo;
                        child.children = null;
                        FlattenTree(item);
                    }
                    else
                    {
                        foreach (var child in item.children)
                            FlattenTree((AssetTreeViewItem)child);
                    }
                }

                foreach (var child in root.children)
                    FlattenTree((AssetTreeViewItem)child);

                // finally calculate depth

                void CalculateDepthAndParent(TreeViewItem item)
                {
                    if (item.children != null)
                    {
                        foreach (var child in item.children)
                        {
                            child.parent = item;
                            child.depth = item.depth + 1;
                            CalculateDepthAndParent(child);
                        }
                    }
                }

                CalculateDepthAndParent(root);

                return root;
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var item = (AssetTreeViewItem)args.item;

                // 切り替えボタンをラベルテキストの左に作成

                EditorGUI.BeginDisabledGroup(!item.IsActive);
                var toggleRect = args.rowRect;
                toggleRect.x += GetContentIndent(args.item);
                toggleRect.width = toggleWidth;
                if (toggleRect.xMax < args.rowRect.xMax)
                {
                    var alt = Event.current.alt;
                    var oldEnabled = item.enabled;
                    item.enabled = EditorGUI.Toggle(toggleRect, item.enabled);
                    // changed while holding alt key, change all children
                    if (oldEnabled != item.enabled && alt) item.SetEnableRecursive(item.enabled);
                }

                // space to toggle checkbox
                if (args.selected)
                {
                    var e = Event.current;
                    if (e is { type: EventType.KeyDown, keyCode: KeyCode.Space })
                    {
                        item.enabled = !item.enabled;
                        if (e.alt)
                        {
                            // set all children
                            item.SetEnableRecursive(item.enabled);
                        }
                        Event.current.Use();
                    }
                }

                EditorGUI.BeginDisabledGroup(!item.enabled);
                base.RowGUI(args);
                EditorGUI.EndDisabledGroup();
                EditorGUI.EndDisabledGroup();
            }
            
            class AssetTreeViewItem : TreeViewItem
            {
                public bool enabled = true;
                public bool readOnly = false;
                public FindResult? AssetInfo;

                public bool IsActive
                {
                    get
                    {
                        // if parent is not active or enabled, this item is not active
                        if (parent is AssetTreeViewItem parentItem)
                        {
                            if (!parentItem.IsActive) return false;
                            if (!parentItem.enabled) return false;
                        }
                        if (readOnly) return false;
                        return true;
                    }
                }

                public void SetEnableRecursive(bool value)
                {
                    enabled = value;
                    if (children != null)
                        foreach (var child in children.Cast<AssetTreeViewItem>())
                            child.SetEnableRecursive(value);
                }
            }
        }

        #endregion
    }
}