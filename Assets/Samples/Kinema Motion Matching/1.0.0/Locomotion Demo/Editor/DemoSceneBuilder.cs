using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kinema.MotionMatching.Samples.Editor
{
    /// <summary>
    /// Reusable pieces of the demo environment: ground, obstacles, camera, sun, materials.
    /// <see cref="DemoSceneTool"/> assembles them, so there is exactly one definition of "what the
    /// demo scene looks like" no matter which source the data was baked from.
    /// </summary>
    public static class DemoSceneBuilder
    {
        #region Main API

        internal static string DemoFolder => DemoPaths.SampleRoot;
        internal static string ScenePath => DemoPaths.ScenePath;

        #endregion

        #region Tools and Utilities — Reusable scene pieces

        internal static Scene NewDemoScene() => EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        /// <summary>
        /// Drops in the cross-character search batch. Registration is not wired here on purpose: the
        /// batch auto-collects every controller in the scene when it enables, so it must not be
        /// handed a list built at author time - one built now would miss anything spawned later, and
        /// ghosts are spawned at runtime.
        /// </summary>
        internal static void BuildSearchBatch()
        {
            var batch = new GameObject("Search Batch");
            batch.AddComponent<MotionMatchingSearchBatch>();
        }

        internal static (Material ground, Material obstacle) CreateMaterials()
        {
            Material ground = CreateLit(DemoFolder + "/Materials/Ground.mat", new Color(0.22f, 0.23f, 0.26f));
            Material obstacle = CreateLit(DemoFolder + "/Materials/Obstacle.mat", new Color(0.30f, 0.72f, 1.00f));
            return (ground, obstacle);
        }

        internal static void BuildEnvironment(Material groundMat, Material obstacleMat)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(6f, 1f, 6f); // 60 x 60 m
            ground.GetComponent<Renderer>().sharedMaterial = groundMat;

            var obstacles = new GameObject("Obstacles");
            CreateBox(obstacles.transform, new Vector3(4f, 0.5f, 3f), new Vector3(2f, 1f, 2f), Quaternion.identity, obstacleMat);
            CreateBox(obstacles.transform, new Vector3(-5f, 1f, 5f), new Vector3(3f, 2f, 1f), Quaternion.Euler(0f, 25f, 0f), obstacleMat);
            CreateBox(obstacles.transform, new Vector3(-3f, 0.25f, -4f), new Vector3(4f, 0.5f, 4f), Quaternion.identity, obstacleMat);
            CreateBox(obstacles.transform, new Vector3(7f, 0.4f, -3f), new Vector3(6f, 0.8f, 1.2f), Quaternion.Euler(0f, -15f, 0f), obstacleMat);
            CreateBox(obstacles.transform, new Vector3(0f, 0.5f, 8f), new Vector3(4f, 0.4f, 5f), Quaternion.Euler(-14f, 0f, 0f), obstacleMat);
        }

        internal static void WireCamera(Transform target)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            cam.transform.position = new Vector3(0f, 3f, -6f);
            cam.transform.rotation = Quaternion.Euler(18f, 0f, 0f);

            var follow = cam.GetComponent<FollowCamera>();
            if (follow == null) follow = cam.gameObject.AddComponent<FollowCamera>();
            SetObjectReference(follow, "_target", target);
        }

        internal static void ConfigureSun()
        {
            foreach (Light light in UnityCompat.FindAll<Light>())
            {
                if (light.type != LightType.Directional) continue;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                light.intensity = 1.1f;
            }
        }

        internal static void CreateBox(Transform parent, Vector3 pos, Vector3 scale, Quaternion rot, Material mat)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.SetParent(parent, false);
            box.transform.SetPositionAndRotation(pos, rot);
            box.transform.localScale = scale;
            box.GetComponent<Renderer>().sharedMaterial = mat;
        }

        internal static Material CreateLit(string path, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            // Idempotent: reuse the material at this path so repeated builds don't spawn "Ground 1.mat".
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        internal static void SetObjectReference(Object component, string property, Object value)
        {
            var so = new SerializedObject(component);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        internal static void SetFloat(Object component, string property, float value)
        {
            var so = new SerializedObject(component);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.floatValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        internal static void SetBool(Object component, string property, bool value)
        {
            var so = new SerializedObject(component);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.boolValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        internal static void SetEnum(Object component, string property, int index)
        {
            var so = new SerializedObject(component);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.enumValueIndex = index;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        internal static void EnsureFolders()
        {
            CreateFolderIfMissing(DemoFolder, "Materials");
        }

        internal static void CreateFolderIfMissing(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }

        #endregion

    }
}
