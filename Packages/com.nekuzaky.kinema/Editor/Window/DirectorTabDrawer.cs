using System;
using UnityEditor;
using UnityEngine;

namespace Kinema.MotionMatching.Editor
{
    /// <summary>
    /// The Director tab: play the data like an animator, record performances, direct ghosts.
    ///
    /// Playback here means clip override - the matcher is suspended and one database clip owns the
    /// pose, with a scrubbable timeline. That is the "custom Animator" view of a motion matching
    /// database: the same inspection you would do in the Animation window, but on the live character
    /// with IK and retargeting applied.
    ///
    /// Recording captures both layers of a performance: intent (session recording - what you asked
    /// for, replayable by ghosts through their own matching) and pose (what ended up on screen,
    /// bakeable to an AnimationClip). Only runtime types are touched, so the tab works with any
    /// character, not just the shipped sample.
    /// </summary>
    public sealed class DirectorTabDrawer
    {
        #region Private and Protected

        private Vector2 _clipScroll;
        private string _clipFilter = "";
        private int _selectedClip = -1;
        private string _saveMessage;
        private bool _loopGhosts = true;
        private bool _exactGhosts = true;
        private SessionRecording _lastRecording;
        private GameObject _swapRig;
        private GameObject _ghostRig;
        private static readonly Color GhostTint = new Color(0.2f, 0.9f, 1f, 1f);

        #endregion

        #region Main API

        public void Draw(MotionMatchingController controller)
        {
            if (controller == null)
            {
                MotionMatchingStyles.HelpRow("Select a character with a MotionMatchingController to direct.", MessageType.Info);
                return;
            }
            if (!Application.isPlaying || !controller.IsInitialized)
            {
                MotionMatchingStyles.HelpRow("Enter Play mode. The Director plays any database clip on the live character, records takes, and sends ghosts out to redo your trajectory.", MessageType.Info);
                DrawRigSwap(controller);
                return;
            }

            DrawPlayback(controller);
            DrawTags(controller);
            DrawRecording(controller);
            DrawTakeTimeline(controller);
            DrawGhosts(controller);
        }

        #endregion

        #region Tools and Utilities — Character

        private void DrawRigSwap(MotionMatchingController controller)
        {
            using (MotionMatchingStyles.BeginSection("Character — swap rig"))
            {
                MotionMatchingStyles.HelpRow(
                    "Replace the character's body in one click. Components, settings and database move over; " +
                    "Humanoid retargeting maps the data onto the new proportions.", MessageType.None);

                _swapRig = (GameObject)EditorGUILayout.ObjectField("New rig", _swapRig, typeof(GameObject), false);

                bool ready = RigSwapUtility.CanSwap(controller, _swapRig, out string reason);
                using (new EditorGUI.DisabledScope(!ready))
                {
                    if (GUILayout.Button("Swap Rig") &&
                        RigSwapUtility.Swap(controller, _swapRig) != null)
                        _swapRig = null;
                }
                if (!ready && _swapRig != null)
                    MotionMatchingStyles.HelpRow(reason, MessageType.Warning);
            }
        }

        #endregion

        #region Tools and Utilities — Playback

        private void DrawPlayback(MotionMatchingController controller)
        {
            MotionMatchingDatabase db = controller.Database;

            using (MotionMatchingStyles.BeginSection("Playback — clip override"))
            {
                bool overriding = controller.IsOverridingClip;
                int playingClip = controller.CurrentClipIndex;

                // Transport row.
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!overriding))
                    {
                        if (GUILayout.Button("|<", GUILayout.Width(32))) controller.SetClipOverrideTime(0f);
                        if (GUILayout.Button(controller.OverridePaused ? "▶" : "||", GUILayout.Width(32)))
                            controller.OverridePaused = !controller.OverridePaused;
                    }

                    if (GUILayout.Button(overriding ? "Back to matching" : "Override current clip", GUILayout.Width(150)))
                    {
                        if (overriding) controller.StopClipOverride();
                        else if (playingClip >= 0) Play(controller, playingClip);
                    }

                    GUILayout.FlexibleSpace();
                    MotionMatchingStyles.StatusPill(overriding ? "DIRECTING" : "MATCHING",
                        overriding ? MotionMatchingStyles.Warning : MotionMatchingStyles.Ok);
                }

                // Timeline: playhead over the active clip, scrubbable while overriding.
                if (playingClip >= 0)
                {
                    MotionClipEntry entry = db.GetClip(playingClip);
                    float length = entry.Clip != null ? entry.Clip.length : entry.FrameCount / (float)db.BakeFrameRate;
                    float time = Mathf.Clamp(controller.CurrentClipTime, 0f, length);

                    GUILayout.Space(2);
                    GUILayout.Label($"{entry.Name}   {time:F2}s / {length:F2}s", MotionMatchingStyles.ValueLabel);

                    Rect rect = GUILayoutUtility.GetRect(120, 22, GUILayout.ExpandWidth(true));
                    DrawTimeline(rect, db, playingClip, time / Mathf.Max(1e-4f, length));

                    if (overriding)
                    {
                        Event e = Event.current;
                        if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
                        {
                            controller.OverridePaused = true;
                            controller.SetClipOverrideTime(Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width) * length);
                            e.Use();
                        }
                    }
                    else
                    {
                        MotionMatchingStyles.HelpRow("Live matching: the playhead shows what the matcher picked. Override to scrub by hand.", MessageType.None);
                    }
                }

                // Clip list.
                _clipFilter = EditorGUILayout.TextField(_clipFilter, EditorStyles.toolbarSearchField);
                _clipScroll = EditorGUILayout.BeginScrollView(_clipScroll, GUILayout.MaxHeight(170));
                for (int i = 0; i < db.ClipCount; i++)
                {
                    string name = db.GetClip(i).Name;
                    if (!string.IsNullOrEmpty(_clipFilter) &&
                        name.IndexOf(_clipFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    bool active = overriding ? i == _selectedClip : i == playingClip;
                    var style = new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };
                    if (active) style.fontStyle = FontStyle.Bold;
                    if (GUILayout.Button($"{i:00}  {name}", style)) Play(controller, i);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void Play(MotionMatchingController controller, int clipIndex)
        {
            _selectedClip = clipIndex;
            controller.PlayClipOverride(clipIndex);
            controller.OverridePaused = false;
        }

        /// <summary>Timeline bar with contact lanes underneath - the animator-style read of a baked clip.</summary>
        private static void DrawTimeline(Rect rect, MotionMatchingDatabase db, int clipIndex, float playhead01)
        {
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));

            // Contact lanes: one thin strip per contact bone, filled where that foot is planted.
            if (db.HasContacts && db.ContactBoneCount > 0)
            {
                MotionClipEntry entry = db.GetClip(clipIndex);
                float laneHeight = 4f;
                for (int bone = 0; bone < db.ContactBoneCount; bone++)
                {
                    float y = rect.yMax - laneHeight * (bone + 1);
                    for (int f = 0; f < entry.FrameCount; f++)
                    {
                        if ((db.GetContacts(entry.StartFrame + f) & (1 << bone)) == 0) continue;
                        float x0 = rect.x + rect.width * (f / (float)entry.FrameCount);
                        float x1 = rect.x + rect.width * ((f + 1) / (float)entry.FrameCount);
                        EditorGUI.DrawRect(new Rect(x0, y, x1 - x0 + 0.5f, laneHeight - 1f), MotionMatchingStyles.Ok * new Color(1, 1, 1, 0.55f));
                    }
                }
            }

            var head = new Rect(rect.x + rect.width * Mathf.Clamp01(playhead01) - 1f, rect.y, 2f, rect.height);
            EditorGUI.DrawRect(head, MotionMatchingStyles.Accent);
        }

        #endregion

        #region Tools and Utilities — Tags

        /// <summary>Stance filter, formerly only in the in-game overlay: the window owns it now.</summary>
        private static void DrawTags(MotionMatchingController controller)
        {
            MotionMatchingDatabase db = controller.Database;
            if (!db.HasTags) return;

            using (MotionMatchingStyles.BeginSection("Tags — require / exclude"))
            {
                string[] names = db.TagNames;
                for (int i = 0; i < names.Length; i++)
                {
                    ulong mask = 1ul << i;
                    bool required = (controller.RequiredTags & mask) != 0;
                    bool excluded = (controller.ExcludedTags & mask) != 0;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(names[i], MotionMatchingStyles.ValueLabel, GUILayout.Width(150));
                        bool nowRequired = GUILayout.Toggle(required, "require", EditorStyles.miniButtonLeft, GUILayout.Width(60));
                        bool nowExcluded = GUILayout.Toggle(excluded, "exclude", EditorStyles.miniButtonRight, GUILayout.Width(60));

                        // Requiring and excluding the same tag would make every frame ineligible.
                        if (nowRequired != required)
                        {
                            controller.RequiredTags = nowRequired ? controller.RequiredTags | mask : controller.RequiredTags & ~mask;
                            if (nowRequired) controller.ExcludedTags &= ~mask;
                        }
                        else if (nowExcluded != excluded)
                        {
                            controller.ExcludedTags = nowExcluded ? controller.ExcludedTags | mask : controller.ExcludedTags & ~mask;
                            if (nowExcluded) controller.RequiredTags &= ~mask;
                        }
                    }
                }
            }
        }

        #endregion

        #region Tools and Utilities — Take timeline

        /// <summary>
        /// Timeline over the recorded take: the speed curve is the "filmstrip", the playhead scrubs
        /// the newest ghost along the recorded trajectory. Scrubbing snaps the ghost's root to the
        /// recorded transform; its matcher then re-solves the pose from there, so the trajectory is
        /// exact and the animation approximate.
        /// </summary>
        private void DrawTakeTimeline(MotionMatchingController controller)
        {
            ReplayLocomotionProvider ghost = NewestGhost(controller);
            // Takes recorded with the in-game hotkeys never pass through this tab, but their ghosts
            // carry the recording - read it off the newest one.
            SessionRecording take = _lastRecording != null && _lastRecording.IsValid ? _lastRecording
                : ghost != null ? ghost.Recording : null;
            if (take == null || !take.IsValid) return;

            using (MotionMatchingStyles.BeginSection($"Take — {take.Duration:F1}s / {take.FrameCount} frames / {take.DistanceTravelled():F1} m"))
            {

                // Speed curve with the playhead over it.
                Rect rect = GUILayoutUtility.GetRect(120, 44, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));
                DrawSpeedCurve(rect, take);

                float progress = ghost != null ? ghost.Progress01 : 0f;
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * progress - 1f, rect.y, 2f, rect.height), MotionMatchingStyles.Accent);

                Event e = Event.current;
                if (ghost != null && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
                {
                    float t = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);
                    ghost.ScrubTo(Mathf.RoundToInt(t * (take.FrameCount - 1)));
                    e.Use();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(ghost == null))
                    {
                        if (GUILayout.Button("|<", GUILayout.Width(32))) ghost.ScrubTo(0);
                        if (GUILayout.Button(ghost != null && ghost.Paused ? "▶" : "||", GUILayout.Width(32)))
                            ghost.Paused = !(ghost != null && ghost.Paused);
                        if (GUILayout.Button("<", GUILayout.Width(28))) ghost.ScrubTo(ghost.FrameIndex - 1);
                        if (GUILayout.Button(">", GUILayout.Width(28))) ghost.ScrubTo(ghost.FrameIndex + 1);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(ghost != null ? $"frame {ghost.FrameIndex}" : "spawn a ghost to scrub the take",
                        MotionMatchingStyles.KeyLabel);
                }
            }
        }

        /// <summary>Horizontal speed per frame, drawn as filled bars - the take's visual fingerprint.</summary>
        private static void DrawSpeedCurve(Rect rect, SessionRecording recording)
        {
            SessionFrame[] frames = recording.Frames;
            int columns = Mathf.Min(frames.Length, Mathf.Max(1, (int)(rect.width / 2f)));
            float max = 0.1f;
            for (int i = 0; i < frames.Length; i++)
            {
                Vector3 v = frames[i].DesiredVelocity;
                max = Mathf.Max(max, new Vector2(v.x, v.z).magnitude);
            }

            float columnWidth = rect.width / columns;
            for (int c = 0; c < columns; c++)
            {
                int f = (int)((long)c * frames.Length / columns);
                Vector3 v = frames[f].DesiredVelocity;
                float h = Mathf.Clamp01(new Vector2(v.x, v.z).magnitude / max) * (rect.height - 4f);
                if (h < 1f) continue;
                EditorGUI.DrawRect(new Rect(rect.x + c * columnWidth, rect.yMax - h - 2f, Mathf.Max(1f, columnWidth - 1f), h),
                    MotionMatchingStyles.Accent * new Color(1f, 1f, 1f, 0.55f));
            }
        }

        /// <summary>
        /// Every ghost records its own pose from spawn. Stop that capture and bake it: the recorded
        /// performance on the ghost's body - swap the rig, spawn, bake, and the take is an
        /// AnimationClip on the new character.
        /// </summary>
        private void BakeGhostClip(MotionMatchingController controller)
        {
            ReplayLocomotionProvider ghost = NewestGhost(controller);
            var recorder = ghost != null ? ghost.GetComponent<PoseRecorder>() : null;
            if (recorder == null)
            {
                _saveMessage = "This ghost carries no pose recorder (spawned before v1.12).";
                return;
            }

            PoseTake take = recorder.IsRecording ? recorder.StopRecording() : recorder.LastTake;
            if (take == null || !take.IsValid)
            {
                _saveMessage = "The ghost has not performed long enough to bake yet.";
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Bake Ghost Performance", "GhostTake", "anim",
                $"{take.FrameCount} frames, {take.Duration:F1}s on '{ghost.name}'.");
            if (string.IsNullOrEmpty(path)) return;

            AnimationClip clip = PoseClipBaker.Bake(take, path);
            if (clip != null)
            {
                _saveMessage = $"Ghost performance baked to {path}.";
                EditorGUIUtility.PingObject(clip);
            }
        }

        private static ReplayLocomotionProvider NewestGhost(MotionMatchingController controller)
        {
            ReplayLocomotionProvider newest = null;
            foreach (ReplayLocomotionProvider replay in UnityCompat.FindAll<ReplayLocomotionProvider>())
                if (replay.gameObject != controller.gameObject)
                    newest = replay;
            return newest;
        }

        #endregion

        #region Tools and Utilities — Recording

        private void DrawRecording(MotionMatchingController controller)
        {
            var session = controller.GetComponent<SessionRecorder>();
            var pose = controller.GetComponent<PoseRecorder>();

            using (MotionMatchingStyles.BeginSection("Record"))
            {
                if (session == null)
                {
                    MotionMatchingStyles.HelpRow("Add a Session Recorder to capture takes.", MessageType.Info);
                    if (GUILayout.Button("Add Session Recorder")) Undo.AddComponent<SessionRecorder>(controller.gameObject);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    MotionMatchingStyles.StatCard(session.RecordedFrameCount.ToString("N0"), "Frames", MotionMatchingStyles.Accent);
                    MotionMatchingStyles.StatCard(session.RecordedDuration.ToString("F1") + "s", "Duration", MotionMatchingStyles.Accent);
                    if (session.IsRecording) MotionMatchingStyles.StatCard("REC", "Capturing", MotionMatchingStyles.Error);
                    else if (_lastRecording != null) MotionMatchingStyles.StatCard(_lastRecording.Duration.ToString("F1") + "s", "Last take", MotionMatchingStyles.Ok);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!session.IsRecording)
                    {
                        if (GUILayout.Button("● Record"))
                        {
                            session.StartRecording();
                            if (pose != null) pose.StartRecording();
                        }
                    }
                    else if (GUILayout.Button("■ Stop"))
                    {
                        session.StopRecording();
                        if (pose != null) pose.StopRecording();
                        if (session.RecordedFrameCount >= 2)
                        {
                            _lastRecording = ScriptableObject.CreateInstance<SessionRecording>();
                            session.WriteTo(_lastRecording, DateTime.UtcNow.ToString("u"));
                        }
                    }

                    using (new EditorGUI.DisabledScope(session.IsRecording || _lastRecording == null))
                        if (GUILayout.Button("Save Session Asset")) SaveSession();

                    using (new EditorGUI.DisabledScope(session.IsRecording || pose == null || pose.LastTake == null || !pose.LastTake.IsValid))
                        if (GUILayout.Button("Bake Animation Clip")) BakeClip(pose.LastTake);
                }

                if (pose == null)
                    MotionMatchingStyles.HelpRow("No Pose Recorder on this character: takes replay as ghosts but cannot bake to an AnimationClip.", MessageType.Info);
                if (!string.IsNullOrEmpty(_saveMessage))
                    MotionMatchingStyles.HelpRow(_saveMessage, MessageType.Info);
            }
        }

        private void SaveSession()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Session Recording", "SessionTake", "asset",
                "Recorded intent: replayable by ghosts and the Replay Locomotion Provider.");
            if (string.IsNullOrEmpty(path)) return;

            var asset = AssetDatabase.LoadAssetAtPath<SessionRecording>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<SessionRecording>();
                AssetDatabase.CreateAsset(asset, path);
            }
            EditorUtility.CopySerialized(_lastRecording, asset);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            _saveMessage = $"Session saved to {path}.";
            EditorGUIUtility.PingObject(asset);
        }

        private void BakeClip(PoseTake take)
        {
            string path = EditorUtility.SaveFilePanelInProject("Bake Take As Animation Clip", "RecordedTake", "anim",
                $"{take.FrameCount} frames, {take.Duration:F1}s, {take.BoneCount} bones.");
            if (string.IsNullOrEmpty(path)) return;

            AnimationClip clip = PoseClipBaker.Bake(take, path);
            if (clip != null)
            {
                _saveMessage = $"Baked {take.Duration:F1}s to {path}. Transform-curve clip: plays on a rig read as Generic; a Humanoid Animator ignores transform curves.";
                EditorGUIUtility.PingObject(clip);
            }
        }

        #endregion

        #region Tools and Utilities — Ghosts

        private void DrawGhosts(MotionMatchingController controller)
        {
            using (MotionMatchingStyles.BeginSection("Ghosts — redo the trajectory"))
            {
                MotionMatchingStyles.HelpRow(
                    "A ghost replays the take's intent through its own matching: it redoes where you went rather than playing a video of you. Two ghosts from one take can differ slightly - that is the system working.",
                    MessageType.None);

                // A different Humanoid model here puts the recorded performance on another body.
                _ghostRig = (GameObject)EditorGUILayout.ObjectField("Ghost rig (optional)", _ghostRig, typeof(GameObject), false);

                SessionRecording take = _lastRecording != null && _lastRecording.IsValid ? _lastRecording : NewestGhost(controller)?.Recording;

                using (new EditorGUILayout.HorizontalScope())
                {
                    _loopGhosts = GUILayout.Toggle(_loopGhosts, "Loop", GUILayout.Width(60));
                    _exactGhosts = GUILayout.Toggle(_exactGhosts, "Exact", GUILayout.Width(64));

                    using (new EditorGUI.DisabledScope(take == null || !take.IsValid))
                    {
                        if (GUILayout.Button("Spawn Ghost"))
                            GhostSpawner.Spawn(controller, take, _loopGhosts, GhostTint, _ghostRig, _exactGhosts);
                    }

                    using (new EditorGUI.DisabledScope(NewestGhost(controller) == null))
                    {
                        if (GUILayout.Button("Bake Ghost Clip")) BakeGhostClip(controller);
                    }

                    if (GUILayout.Button("Clear Ghosts"))
                    {
                        foreach (ReplayLocomotionProvider replay in UnityCompat.FindAll<ReplayLocomotionProvider>())
                            if (replay.gameObject != controller.gameObject)
                                UnityEngine.Object.Destroy(replay.gameObject);
                    }
                }

                if (take == null)
                    MotionMatchingStyles.HelpRow("Record a take first - the in-game shortcuts (R / gamepad Select) feed the same recorders.", MessageType.Info);
            }
        }

        #endregion
    }
}
