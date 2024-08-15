using UnityEditor;

namespace Anatawa12.VRCConstraintsConverter
{
    public class ConverterWindow : EditorWindow
    {
        [MenuItem("Tools/Project Wide VRC Constraints Converter")]
        public static void ShowWindow() => GetWindow<ConverterWindow>("VRC Constraints Converter");

        private void OnGUI()
        {
            // TODO
        }
    }
}