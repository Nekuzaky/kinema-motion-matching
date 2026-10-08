using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Kinema.MotionMatching.Editor
{
    /// <summary>
    /// One command that answers "what is your setup actually doing", as plain text you can paste.
    ///
    /// It exists because the numbers that decide every real question here - is the search thrashing,
    /// is the character drifting, is the pose cost the naive one, does the bake carry mirrored frames
    /// - live in six different places, and reading them off a screen recording is guesswork. Every
    /// field below is here because working out its value the hard way cost someone a round trip.
    ///
    /// It reports; it never fixes. Anything that reads the world honestly has to be safe to run when
    /// the world is broken.
    /// </summary>
    public static class KinemaDiagnostics
    {
        #region Main API

        [MenuItem("Tools/Kinema/Copy Diagnostics", priority = 100)]
        public static void CopyDiagnostics()
        {
            string report = Build();
            EditorGUIUtility.systemCopyBuffer = report;
            Debug.Log(report);
        }

        /// <summary>The report, for the window's button and for tests.</summary>
        public static string Build()
        {
            var text = new StringBuilder();
            text.AppendLine("===== KINEMA DIAGNOSTICS =====");
            AppendEnvironment(text);

            var controllers = UnityCompat.FindAll<MotionMatchingController>();
            if (controllers.Length == 0)
            {
                text.AppendLine();
                text.AppendLine("No MotionMatchingController in the open scene.");
                return text.ToString();
            }

            foreach (MotionMatchingController controller in controllers) AppendCharacter(text, controller);

            text.AppendLine();
            text.AppendLine("===== END =====");
            return text.ToString();
        }

        #endregion

        #region Tools and Utilities

        private static void AppendEnvironment(StringBuilder text)
        {
            text.AppendLine($"package    : {PackageVersion()}");
            text.AppendLine($"unity      : {Application.unityVersion}");
            text.AppendLine($"scene      : {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            text.AppendLine($"play mode  : {Application.isPlaying}");

            // Said out loud because the live numbers below are the whole point, and in edit mode they
            // are all zero - which reads as "everything is calm" rather than "nothing has run".
            if (!Application.isPlaying)
                text.AppendLine("             (edit mode: every live figure below is zero because nothing has ticked)");
        }

        private static void AppendCharacter(StringBuilder text, MotionMatchingController controller)
        {
            text.AppendLine();
            text.AppendLine($"--- {controller.name} ---");

            MotionMatchingDatabase database = controller.Database;
            if (database == null)
            {
                text.AppendLine("database   : NONE  <- nothing can work without one");
                return;
            }

            AppendDatabase(text, database);
            AppendComponents(text, controller);
            AppendTags(text, controller, database);
            if (Application.isPlaying && controller.IsInitialized) AppendLive(text, controller);
        }

        private static void AppendDatabase(StringBuilder text, MotionMatchingDatabase database)
        {
            FeatureSchema schema = database.Schema;
            text.AppendLine($"database   : {database.name}");
            text.AppendLine($"  frames   : {database.FrameCount:N0} in {database.ClipCount} clips, {database.Dimension} dims");
            text.AppendLine($"  mirrored : {(database.HasMirroredFrames ? "yes" : "NO  <- half the coverage a free rebake would give")}");
            text.AppendLine($"  tags     : {(database.HasTags ? string.Join(",", database.TagNames) : "NONE  <- nothing can be filtered out of the search")}");
            text.AppendLine($"  pose mode: {schema.PoseMode}" +
                            (schema.PoseMode == PoseCostMode.Naive
                                ? "  <- hand-weighted pos+vel; InertializationCost is smaller and needs no velocity weight"
                                : $"  (halflife {schema.InertializationHalflife.ToString("F2", CultureInfo.InvariantCulture)})"));
            text.AppendLine($"  schema   : T={schema.TrajectoryPointCount} B={schema.BoneCount} [{string.Join(",", schema.BoneNames)}]");
        }

        private static void AppendComponents(StringBuilder text, MotionMatchingController controller)
        {
            // Presence, not settings. Every "the AI does random things" and "it walks like it is
            // sitting" so far has been a component that was missing from one character and not
            // another, and no inspector shows you two characters at once.
            var present = new StringBuilder();
            Add(present, controller.GetComponent<FootLockIK>(), "FootLockIK");
            Add(present, controller.GetComponent<GroundAdaptationIK>(), "GroundAdaptationIK");
            Add(present, controller.GetComponent<ObstacleSensor>(), "ObstacleSensor");
            Add(present, controller.GetComponent<LocomotionTagFilter>(), "LocomotionTagFilter");
            Add(present, controller.GetComponent<MotionMatchingLOD>(), "LOD");
            Add(present, controller.GetComponent<MotionQualityProbe>(), "QualityProbe");
            Add(present, controller.GetComponent<AICommandProvider>(), "AICommandProvider");

            text.AppendLine($"  parts    : {(present.Length == 0 ? "(none)" : present.ToString())}");
            text.AppendLine($"  batch    : {(UnityCompat.FindFirst<MotionMatchingSearchBatch>() != null ? "yes" : "no")}");
        }

        private static void AppendTags(StringBuilder text, MotionMatchingController controller, MotionMatchingDatabase database)
        {
            if (!database.HasTags || !Application.isPlaying) return;

            // Resolved masks, not the names someone typed: a tag spelled wrong resolves to zero and
            // excludes nothing, and looks identical in the inspector to one that works.
            text.AppendLine($"  filters  : required=0x{controller.RequiredTags:X} excluded=0x{controller.ExcludedTags:X}" +
                            (controller.ExcludedTags == 0ul
                                ? "  <- nothing excluded: crouch and jump clips are competing for every step"
                                : ""));
        }

        private static void AppendLive(StringBuilder text, MotionMatchingController controller)
        {
            MotionMatchingDebugData debug = controller.LastDebug;
            float measured = Flat(controller.MeasuredVelocity).magnitude;
            float wanted = Flat(controller.DesiredVelocity).magnitude;
            float jumpRate = controller.TotalSearches > 0 ? 100f * controller.TotalJumps / controller.TotalSearches : 0f;

            text.AppendLine($"  playing  : {debug.SelectedClipName} f{debug.SelectedFrame}");
            text.AppendLine($"  speed    : {F(measured)} / {F(wanted)} m/s   warp {F(controller.CurrentStrideWarp)}x" +
                            (wanted < 0.05f && measured > 0.2f ? "   <- DRIFT: moving with nothing asked of it" : ""));
            text.AppendLine($"  cost     : {F(debug.TotalCost)}  (traj {F(debug.TrajectoryCost)} / pose {F(debug.PoseCost)})" +
                            (controller.IsPlayingEvent ? "   (mid-event: the search is not driving this pose)" : ""));
            text.AppendLine($"  searches : {controller.TotalSearches:N0}, jumped {controller.TotalJumps:N0}  = jump rate {jumpRate:F0}%");
            text.AppendLine($"  blending : {controller.BlendFraction * 100f:F0}%   <- share of life spent as the average of two clips");

            var probe = controller.GetComponent<MotionQualityProbe>();
            if (probe != null)
                text.AppendLine($"  footslide: {probe.FootSlideRate:F3} m/s mean, {probe.PeakFootSlideRate:F3} peak" +
                                "   (under ~0.05 reads as planted)");
        }

        private static void Add(StringBuilder builder, Object component, string label)
        {
            if (component == null) return;
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(label);
        }

        private static string PackageVersion()
        {
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MotionMatchingController).Assembly);
            return info != null ? $"{info.name} {info.version}" : "(not resolved as a package)";
        }

        private static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        #endregion
    }
}
