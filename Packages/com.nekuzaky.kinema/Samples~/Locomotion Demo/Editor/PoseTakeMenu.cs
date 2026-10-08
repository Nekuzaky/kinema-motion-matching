using Kinema.MotionMatching.Editor;
using UnityEditor;
using UnityEngine;

namespace Kinema.MotionMatching.Samples.Editor
{
    /// <summary>
    /// Saves the last recorded performance as an AnimationClip asset.
    ///
    /// Must run in play mode: a take lives in memory on the recorder, because assets cannot be
    /// written while the game is running. Bake before leaving play mode or the take is gone.
    /// </summary>
    public static class PoseTakeMenu
    {
        #region Main API

        [MenuItem("Tools/Kinema/Save Last Take As Animation Clip", priority = 61)]
        public static void SaveTake()
        {
            PoseRecorder recorder = FindRecorderWithTake();
            if (recorder == null)
            {
                EditorUtility.DisplayDialog("Kinema",
                    "No recorded take found.\n\nEnter play mode, press R (or the Record button in the browser " +
                    "overlay) to start recording, move around, then press R again. Bake before leaving play mode - " +
                    "the take lives in memory and does not survive the exit.", "OK");
                return;
            }

            PoseTake take = recorder.LastTake;
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Take As Animation Clip",
                "RecordedTake", "anim",
                $"{take.FrameCount} frames, {take.Duration:F1}s, {take.BoneCount} bones.");
            if (string.IsNullOrEmpty(path)) return;

            AnimationClip clip = PoseClipBaker.Bake(take, path);
            if (clip != null) EditorGUIUtility.PingObject(clip);
        }

        [MenuItem("Tools/Kinema/Save Last Take As Animation Clip", validate = true)]
        private static bool ValidateSaveTake() => Application.isPlaying;

        #endregion

        #region Tools and Utilities

        private static PoseRecorder FindRecorderWithTake()
        {
            foreach (PoseRecorder recorder in UnityCompat.FindAll<PoseRecorder>())
                if (recorder.LastTake != null && recorder.LastTake.IsValid)
                    return recorder;
            return null;
        }

        #endregion
    }
}
