using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if KINEMA_URP
using UnityEngine.Rendering.Universal;
#endif

namespace Kinema.MotionMatching.Samples.Editor
{
    /// <summary>
    /// Lifts the demo scene out of programmer-art: physically-plausible materials, soft shadows and
    /// gradient ambient, and a URP post-processing volume (tonemapping, bloom, vignette, colour
    /// grading). None of it needs an imported art asset - it is all shader values, light settings and
    /// a generated volume profile - so the whole look regenerates with the scene.
    ///
    /// SSAO is deliberately left out: it is a renderer feature on the URP renderer asset, not a
    /// volume override, so a scene builder cannot add it without editing the pipeline asset. The
    /// volume effects here are the ones a scene can own.
    /// </summary>
    public static class DemoPresentation
    {
        #region Main API

        private static string ProfilePath => DemoPaths.SampleRoot + "/Materials/DemoPostProcess.asset";

        /// <summary>Applies the full look to a freshly built scene: lighting, ambient, post, character skin.</summary>
        public static void Apply(GameObject character, Camera camera)
        {
            ConfigureLighting();
            ConfigureAmbient();
            ConfigurePostProcessing(camera);
            ApplyCharacterMaterial(character);
        }

        /// <summary>Ground and obstacle materials, tuned as real surfaces rather than flat fills.</summary>
        public static (Material ground, Material obstacle) CreateMaterials()
        {
            // Ground: dark, near-matte - reads as concrete, and lets the character and shadows pop.
            Material ground = Lit(DemoPaths.Materials + "/Ground.mat", new Color(0.16f, 0.17f, 0.19f), smoothness: 0.08f);
            // Obstacles: a muted slate blue, a little sheen - present but not the neon cyan of before.
            Material obstacle = Lit(DemoPaths.Materials + "/Obstacle.mat", new Color(0.32f, 0.40f, 0.52f), smoothness: 0.25f);
            return (ground, obstacle);
        }

        #endregion

        #region Tools and Utilities — Lighting

        private static void ConfigureLighting()
        {
            foreach (Light light in UnityCompat.FindAll<Light>())
            {
                if (light.type != LightType.Directional) continue;

                // A low, warm key light throws long readable shadows - the single biggest tell that a
                // character is grounded, which is the whole point of a locomotion demo.
                light.transform.rotation = Quaternion.Euler(42f, -138f, 0f);
                light.color = new Color(1f, 0.96f, 0.86f);
                light.intensity = 1.35f;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.75f;
                light.shadowBias = 0.05f;
                light.shadowNormalBias = 0.4f;
            }
        }

        private static void ConfigureAmbient()
        {
            // Gradient ambient: a cool sky and a warmer bounce off the ground give the shaded side of
            // the character depth instead of a flat fill. Cheap, and it reads as global illumination.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.52f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.30f, 0.31f, 0.33f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.14f, 0.12f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = false;
        }

        #endregion

        #region Tools and Utilities — Post-processing

        private static void ConfigurePostProcessing(Camera camera)
        {
#if KINEMA_URP
            VolumeProfile profile = BuildProfile();

            var volumeGo = new GameObject("Post Process Volume");
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            // sharedProfile, not profile: the profile getter instantiates a runtime copy that the
            // scene does not serialize, so the saved scene would reference no profile and post-
            // processing would silently do nothing. sharedProfile assigns the asset itself.
            volume.sharedProfile = profile;

            if (camera == null) return;
            camera.allowHDR = true;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
                data.dithering = true;
            }
#else
            // Built-in or HDRP project: the volume overrides below are URP types. The lighting,
            // ambient and materials still apply; only the post stack is skipped.
            Debug.Log("[Kinema] Demo: URP is not installed, so the post-processing volume is skipped.");
#endif
        }

#if KINEMA_URP
        /// <summary>Generates (or rewrites) the volume profile asset and its overrides.</summary>
        private static VolumeProfile BuildProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            else
            {
                // Rebuild from clean so repeated runs do not stack duplicate components.
                for (int i = profile.components.Count - 1; i >= 0; i--)
                    Object.DestroyImmediate(profile.components[i], true);
                profile.components.Clear();
            }

            // Filmic tonemapping: the difference between "raw lit" and "graded" at a glance.
            var tonemapping = Add<Tonemapping>(profile);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;

            var bloom = Add<Bloom>(profile);
            bloom.intensity.overrideState = true; bloom.intensity.value = 0.5f;
            bloom.threshold.overrideState = true; bloom.threshold.value = 1.1f;
            bloom.scatter.overrideState = true; bloom.scatter.value = 0.6f;

            var color = Add<ColorAdjustments>(profile);
            color.postExposure.overrideState = true; color.postExposure.value = 0.15f;
            color.contrast.overrideState = true; color.contrast.value = 12f;
            color.saturation.overrideState = true; color.saturation.value = 6f;

            var vignette = Add<Vignette>(profile);
            vignette.intensity.overrideState = true; vignette.intensity.value = 0.26f;
            vignette.smoothness.overrideState = true; vignette.smoothness.value = 0.4f;

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        /// <summary>
        /// VolumeProfile.Add creates the override in memory and lists it, but does not persist it -
        /// a scripted profile serialized its components as null (fileID 0) until each one was also
        /// added to the profile asset as a sub-object.
        /// </summary>
        private static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            T component = profile.Add<T>(true);
            component.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }
#endif

        #endregion

        #region Tools and Utilities — Materials

        private static void ApplyCharacterMaterial(GameObject character)
        {
            if (character == null) return;
            Material skin = Lit(DemoPaths.Materials + "/CharacterSkin.mat", new Color(0.78f, 0.74f, 0.70f), smoothness: 0.28f);
            foreach (Renderer renderer in character.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = skin;
        }

        private static Material Lit(string path, Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

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
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        #endregion
    }
}
