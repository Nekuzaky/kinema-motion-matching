using System.Collections.Generic;
using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Records what you do, then sends an NPC out to do it again.
    ///
    /// Two captures run at once because they answer different questions. The session recording holds
    /// your *intent* - the velocity you asked for each frame - and the ghost is driven by that,
    /// re-running its own matching against it. So the ghost is not a video of you: it makes its own
    /// decisions and only reproduces the trajectory. Two ghosts spawned from one take can differ
    /// slightly, and that is the system working, not a bug.
    ///
    /// The pose recording holds the *result* - the actual bones after matching, blending and IK - and
    /// is what gets baked into an AnimationClip, where an exact record is the whole point.
    ///
    /// This component is the in-game control surface (keyboard and gamepad); the spawning itself
    /// lives in <see cref="GhostSpawner"/> so the editor window shares the exact same code path.
    /// Ghosts keep no collision motor, so they pass through obstacles - the traditional ghost rule.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Ghost Replay Director")]
    [RequireComponent(typeof(MotionMatchingController))]
    [RequireComponent(typeof(SessionRecorder))]
    public sealed class GhostReplayDirector : MonoBehaviour
    {
        #region Public

        [Tooltip("Ghosts loop their take instead of stopping at the end.")]
        [SerializeField] private bool _loopGhosts = true;

        [Tooltip("Send a ghost out automatically as soon as a recording stops.")]
        [SerializeField] private bool _spawnOnStop = true;

        [SerializeField] private Color _ghostColor = new Color(0.2f, 0.9f, 1f, 1f);

        [Tooltip("Optional: build ghosts on this Humanoid model instead of cloning the player.")]
        [SerializeField] private GameObject _ghostRig;

        [Tooltip("Exact: the ghost reproduces the recorded transform and frames precisely. Off: it re-matches the recorded intent through its own search (the 'AI redoes your trajectory' behaviour), which can differ slightly.")]
        [SerializeField] private bool _exactGhosts = true;

        public bool IsRecording => _sessionRecorder.IsRecording;
        public int GhostCount { get { PruneGhosts(); return _ghosts.Count; } }
        public float LastTakeDuration => _lastRecording != null ? _lastRecording.Duration : 0f;

        /// <summary>The take ghosts replay. Runtime instance; the editor saves it to disk if wanted.</summary>
        public SessionRecording LastRecording => _lastRecording;

        /// <summary>Pose take of the last recording, for the editor to bake into a clip. Null until one exists.</summary>
        public PoseTake LastPoseTake => _poseRecorder != null ? _poseRecorder.LastTake : null;

        #endregion

        #region Private and Protected

        private MotionMatchingController _controller;
        private SessionRecorder _sessionRecorder;
        private PoseRecorder _poseRecorder;
        private SessionRecording _lastRecording;
        private readonly List<GameObject> _ghosts = new();

        private DemoButton _recordAction;
        private DemoButton _ghostAction;
        private DemoButton _clearAction;

        #endregion

        #region Unity API

        private void Awake()
        {
            _controller = GetComponent<MotionMatchingController>();
            _sessionRecorder = GetComponent<SessionRecorder>();
            _poseRecorder = GetComponent<PoseRecorder>();

            // Keyboard and gamepad both drive the director; K not C for clear (C is the crouch stance).
            _recordAction = DemoInput.Button("Record",
                new[] { "<Keyboard>/r", "<Gamepad>/selectButton" }, KeyCode.R, KeyCode.JoystickButton6);
            _ghostAction = DemoInput.Button("SpawnGhost",
                new[] { "<Keyboard>/g", "<Gamepad>/rightShoulder" }, KeyCode.G, KeyCode.JoystickButton5);
            _clearAction = DemoInput.Button("ClearGhosts",
                new[] { "<Keyboard>/k", "<Gamepad>/dpad/down" }, KeyCode.K);
        }

        private void OnEnable() { _recordAction.Enable(); _ghostAction.Enable(); _clearAction.Enable(); }
        private void OnDisable() { _recordAction.Disable(); _ghostAction.Disable(); _clearAction.Disable(); }

        private void Update()
        {
            // The browser overlay has a text field; typing in it must not fire these shortcuts.
            if (GUIUtility.keyboardControl != 0) return;

            if (_recordAction.WasPressedThisFrame()) ToggleRecording();
            if (_ghostAction.WasPressedThisFrame()) SpawnGhost();
            if (_clearAction.WasPressedThisFrame()) ClearGhosts();
        }

        #endregion

        #region Main API

        public void ToggleRecording()
        {
            if (IsRecording) StopRecording();
            else StartRecording();
        }

        public void StartRecording()
        {
            _sessionRecorder.StartRecording();
            if (_poseRecorder != null) _poseRecorder.StartRecording();
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            _sessionRecorder.StopRecording();
            if (_poseRecorder != null) _poseRecorder.StopRecording();

            if (_sessionRecorder.RecordedFrameCount < 2)
            {
                Debug.LogWarning("[Kinema] Take too short to replay; discarded.", this);
                return;
            }

            // A runtime ScriptableObject: assets cannot be written in play mode, and the ghost only
            // needs the data in memory. Saving it to disk is the editor's job.
            _lastRecording = ScriptableObject.CreateInstance<SessionRecording>();
            _sessionRecorder.WriteTo(_lastRecording, "runtime");

            Debug.Log($"[Kinema] Recorded {_lastRecording.FrameCount} frames, {_lastRecording.Duration:F1}s, " +
                      $"{_lastRecording.DistanceTravelled():F1} m travelled.", this);

            if (_spawnOnStop) SpawnGhost();
        }

        public void SpawnGhost()
        {
            if (_lastRecording == null || !_lastRecording.IsValid)
            {
                Debug.LogWarning("[Kinema] Nothing recorded yet - record a take first (R / gamepad Select).", this);
                return;
            }

            GameObject ghost = GhostSpawner.Spawn(_controller, _lastRecording, _loopGhosts, _ghostColor, _ghostRig, _exactGhosts);
            if (ghost == null) return;

            PruneGhosts();
            _ghosts.Add(ghost);
            ghost.name = $"Ghost {_ghosts.Count}";
            Debug.Log($"[Kinema] {ghost.name} replaying {_lastRecording.Duration:F1}s of intent through its own matching.", this);
        }

        public void ClearGhosts()
        {
            foreach (GameObject ghost in _ghosts)
                if (ghost != null) Destroy(ghost);
            _ghosts.Clear();
        }

        #endregion

        #region Tools and Utilities

        private void PruneGhosts() => _ghosts.RemoveAll(g => g == null);

        #endregion
    }
}
