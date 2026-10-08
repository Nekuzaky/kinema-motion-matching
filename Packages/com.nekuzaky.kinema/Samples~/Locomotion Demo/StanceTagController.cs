using Kinema.MotionMatching;
using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Drives the matcher's tag filter from gameplay stance.
    ///
    /// A real mocap pack mixes stances in one database - the Opsive locomotion set is ~44% crouch.
    /// Nothing in the feature vector says "this pose is crouched", so an unfiltered search will
    /// happily answer a standing query with a crouched frame whose feet and trajectory happen to
    /// match. Tags are the fix: the search only considers frames carrying the stance we are in.
    ///
    /// Airborne clips are excluded outright: jumps belong to the event system, not to the
    /// continuous locomotion search.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Stance Tag Controller")]
    [RequireComponent(typeof(MotionMatchingController))]
    public sealed class StanceTagController : MonoBehaviour
    {
        #region Public

        [Tooltip("Tag marking crouched frames in the database.")]
        [SerializeField] private string _crouchTag = "Crouch";

        [Tooltip("Tags never eligible for the locomotion search (airborne motion is event-driven).")]
        [SerializeField] private string[] _alwaysExcluded = { "Jump" };

        [Tooltip("Shrink the character controller while crouched so low gaps are passable.")]
        [SerializeField] private bool _resizeCollider = true;

        [SerializeField, Range(0.6f, 1.6f)] private float _crouchHeight = 1.2f;

        public bool IsCrouching { get; private set; }

        #endregion

        #region Private and Protected

        private MotionMatchingController _controller;
        private CharacterController _characterController;
        private DemoButton _crouchAction;
        private ulong _crouchMask, _excludedMask;
        private float _standHeight;
        private Vector3 _standCenter;
        private bool _resolved;

        #endregion

        #region Unity API

        private void Awake()
        {
            _controller = GetComponent<MotionMatchingController>();
            _characterController = GetComponent<CharacterController>();
            if (_characterController != null)
            {
                _standHeight = _characterController.height;
                _standCenter = _characterController.center;
            }

            _crouchAction = DemoInput.Button("Crouch",
                new[] { "<Keyboard>/c", "<Keyboard>/leftCtrl", "<Gamepad>/buttonEast" },
                KeyCode.C, KeyCode.LeftControl, KeyCode.JoystickButton1);
        }

        private void OnEnable() => _crouchAction.Enable();
        private void OnDisable() => _crouchAction.Disable();

        private void Update()
        {
            if (!_controller.IsInitialized) return;
            if (!_resolved) ResolveMasks();

            bool crouch = _crouchAction.IsPressed();
            if (crouch != IsCrouching) SetCrouch(crouch);
        }

        #endregion

        #region Tools and Utilities

        private void ResolveMasks()
        {
            _resolved = true;
            MotionMatchingDatabase db = _controller.Database;
            if (db == null || !db.HasTags)
            {
                Debug.LogWarning($"[Kinema] '{name}': the database carries no tags, so stance filtering does nothing. Rebake with tag ranges authored.", this);
                return;
            }

            _crouchMask = db.GetTagMask(_crouchTag);
            foreach (string tag in _alwaysExcluded) _excludedMask |= db.GetTagMask(tag);
            SetCrouch(false);
        }

        private void SetCrouch(bool crouch)
        {
            IsCrouching = crouch;

            // Standing: forbid crouched frames. Crouching: demand them. Airborne is always out.
            _controller.RequiredTags = crouch ? _crouchMask : 0ul;

            // Rebuilt from this component's own two masks rather than or-ed into whatever was there:
            // crouch has to be able to leave the excluded set when the player crouches, and an
            // accumulating mask could never let go of it. Anything else owning exclusions on the same
            // character would be clobbered here - which is why an AI gets a LocomotionTagFilter and
            // not this.
            _controller.ExcludedTags = _excludedMask | (crouch ? 0ul : _crouchMask);

            if (!_resizeCollider || _characterController == null) return;
            float height = crouch ? _crouchHeight : _standHeight;
            _characterController.height = height;
            _characterController.center = new Vector3(_standCenter.x, height * 0.5f, _standCenter.z);
        }

        #endregion
    }
}
