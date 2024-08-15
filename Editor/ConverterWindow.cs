using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Anatawa12.VRCConstraintsConverter
{
    public class ConverterWindow : EditorWindow
    {
        [MenuItem("Tools/Project Wide VRC Constraints Converter")]
        public static void ShowWindow() => GetWindow<ConverterWindow>("VRC Constraints Converter");

        Vector2 scrollPosition;
        Vector2 errorScrollPosition;
        FindResult[] assetsToConvert;
        List<ErrorForObject> errors;

        TimeSpan lastSearchTime;

        private bool openDebugMenu;
        private AnimationClip clip;
        private GameObject gameObject;
        private Behaviour constraint;

        private void OnGUI()
        {
            // TODO
            if (GUILayout.Button("Search for files"))
            {
                OnSearchFiles();
            }

            EditorGUILayout.LabelField($"Last search time: {lastSearchTime}");
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            assetsToConvert ??= Array.Empty<FindResult>();
            if (assetsToConvert != null)
            {
                foreach (var file in assetsToConvert)
                {
                    // TODO: show in a better way (like tree view)
                    EditorGUILayout.LabelField(file.Path);
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField("Errors:");
            errorScrollPosition = EditorGUILayout.BeginScrollView(errorScrollPosition);
            errors ??= new List<ErrorForObject>();
            foreach (var error in errors)
            {
                EditorGUILayout.LabelField($"{error.obj.name}: {error.error}");
            }

            if (errors.Count == 0)
            {
                EditorGUILayout.LabelField("No errors at this time");
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Convert Animation Clips"))
            {
                foreach (var asset in assetsToConvert)
                {
                    if (asset.Type != AsseetType.Asset) continue;
                    foreach (var obj in asset.Objects)
                    {
                        if (obj is AnimationClip animationClip)
                        {
                            Undo.RecordObject(animationClip, "Convert Unity Constraints to VRC Constraints");
                            ConvertAnimationClip(animationClip, true);
                        }
                    }
                }
            }

            openDebugMenu = EditorGUILayout.Foldout(openDebugMenu, "Debug");
            if (openDebugMenu)
            {
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
                if (gameObject)
                {
                    if (GUILayout.Button("Convert Whole GameObject"))
                    {
                        Undo.IncrementCurrentGroup();
                        var group = Undo.GetCurrentGroup();
                        ConvertGameObject(gameObject);
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
                        ConvertConstraint(iconstraint);
                    }
                }
            }
        }

        void OnSearchFiles()
        {
            var stopwatch = Stopwatch.StartNew();
            assetsToConvert = FindAssetsForConversion();
            stopwatch.Stop();
            lastSearchTime = stopwatch.Elapsed;
        }

        #region Find Assets For Conversion

        class FindResult
        {
            public string Path;
            public AsseetType Type;
            public Object[] Objects;

            public static FindResult Scene(string assetPath) => new() { Path = assetPath, Type = AsseetType.Scene };

            public static FindResult Prefab(string assetPath, GameObject prefab) => new()
                { Path = assetPath, Type = AsseetType.Prefab, Objects = new Object[] { prefab } };

            public static FindResult Asset(string assetPath, Object[] assetFiles) => new()
                { Path = assetPath, Type = AsseetType.Asset, Objects = assetFiles };
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
                            if (prefab.GetComponentsInChildren<IConstraint>().Any())
                            {
                                filesToConvert.Add(FindResult.Prefab(assetPath, prefab));
                            }

                            break;

                        case ".asset":
                        case ".controller":
                        case ".mesh":
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
                errors.Add(new ErrorForObject
                    { obj = clip, error = $"Unsupported properties: {string.Join(", ", unsupportedProperties)}" });
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

        static bool TryMapConstraintProperty(string propertyName, out string newPropertyName, out bool supported)
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

        static void ConvertGameObject(GameObject gameObject)
        {
            Undo.SetCurrentGroupName("Convert Unity Constraints to VRC Constraints");
            foreach (var constraint in gameObject.GetComponentsInChildren<IConstraint>())
                ConvertConstraint(constraint);
        }

        private static void ConvertConstraint(IConstraint constraint)
        {
            var oldConstraint = (Behaviour)constraint;

            if (!ConstraintTypeMapping.TryGetValue(oldConstraint.GetType(), out var newType))
            {
                Debug.LogError("Unsupported constraint type: " + oldConstraint.GetType(), oldConstraint);
                return;
            }

            var targetGameObject = oldConstraint.gameObject;

            // add component to position
            var newComponent = (VRCConstraintBase)Undo.AddComponent(targetGameObject, newType);
            MoveComponentRelativeToComponent(newComponent, oldConstraint, true);
            Undo.RecordObject(newComponent, "Convert Unity Constraints to VRC Constraints");

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

            // finally, remove old component
            Undo.DestroyObjectImmediate(oldConstraint);
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
            public Object obj;
            public string error;
        }

        #region Reflection Util

        private static readonly MethodInfo MoveComponentRelativeToComponentMethodInfo =
            typeof(UnityEditorInternal.ComponentUtility)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .First(e => e.Name == "MoveComponentRelativeToComponent" && e.GetParameters().Length == 3);

        internal static void MoveComponentRelativeToComponent(Component component, Component targetComponent, bool aboveTarget)
        {
            MoveComponentRelativeToComponentMethodInfo.Invoke(null, new object[] { component, targetComponent, aboveTarget });
        }

        #endregion
    }
}