using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Sample locomotion driver: turns WASD / left-stick input into a camera-relative desired
    /// velocity and feeds it to the controller through <see cref="ILocomotionProvider"/>. Kept in the
    /// Samples assembly so the runtime stays free of an Input System dependency and studios can swap
    /// in their own intent source.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Locomotion Input Provider")]
    [RequireComponent(typeof(MotionMatchingController))]
    public sealed class LocomotionInputProvider : MonoBehaviour, ILocomotionProvider
    {
        #region Public

        // 3 m/s, not 4: measured against the Opsive set, ~6.5% of frames sit within stride-warp range
        // of a 3 m/s request versus ~2.8% at 4 m/s. Asking for a speed the data barely holds starves
        // the search, so it flickers between clips and the stride warp pins at its 1.3x ceiling -
        // both of which slide the planted foot. This keeps the demand inside the data.
        [Tooltip("Top locomotion speed. Keep it inside the range the database actually covers; asking " +
                 "for more starves the search and slides the feet.")]
        [SerializeField, Min(0f)] private float _maxSpeed = 3f;
        [Tooltip("Movement is relative to this transform's yaw. Defaults to the main camera.")]
        [SerializeField] private Transform _cameraTransform;
        [Tooltip("How quickly the desired velocity chases the input. Higher = snappier.")]
        [SerializeField, Min(0f)] private float _inputSharpness = 12f;

        [Tooltip("Stick magnitude below this reads as centred - kills gamepad drift walking the character off on its own.")]
        [SerializeField, Range(0f, 0.5f)] private float _stickDeadzone = 0.2f;

        public Vector3 DesiredVelocity => _desiredVelocity;

        /// <summary>
        /// Strafe mode (hold right mouse / gamepad left trigger): face the camera forward while
        /// moving in any direction. Otherwise zero, letting the controller face the travel direction.
        /// </summary>
        public Vector3 DesiredFacing
        {
            get
            {
                if (!_strafe || _cameraTransform == null) return Vector3.zero;
                Vector3 fwd = _cameraTransform.forward;
                fwd.y = 0f;
                return fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.zero;
            }
        }

        public bool IsStrafing => _strafe;

        #endregion

        #region Private and Protected

        private DemoVector _moveAction;
        private DemoButton _strafeAction;
        private Vector3 _desiredVelocity;
        private bool _strafe;

        #endregion

        #region Unity API

        private void Awake()
        {
            _moveAction = DemoInput.Move();
            _strafeAction = DemoInput.Button("Strafe",
                new[] { "<Mouse>/rightButton", "<Gamepad>/leftTrigger" }, KeyCode.Mouse1);

            if (_cameraTransform == null && Camera.main != null)
                _cameraTransform = Camera.main.transform;
        }

        private void OnEnable() { _moveAction.Enable(); _strafeAction.Enable(); }
        private void OnDisable() { _moveAction.Disable(); _strafeAction.Disable(); }

        private void Update()
        {
            _strafe = _strafeAction.IsPressed();
            Vector2 move = _moveAction.ReadValue();

            // Deadzone: a gamepad stick rarely rests at exactly zero, and even 5% drift here would
            // feed a constant desired velocity - the character walks off with no input touched, the
            // "it moves on its own at Play" report. Below the threshold the stick reads as centred;
            // keyboard input is 0 or 1 so this never affects it.
            //
            // Rescaled past the threshold rather than passed through: a stick just outside a 0.2
            // deadzone used to ask for 20% of top speed at once, so a thumb resting near the edge
            // toggled the request between 0 and 0.6 m/s - idle and walk flickering in turn. Now the
            // request starts from zero at the deadzone and reaches full speed at full tilt.
            float tilt = move.magnitude;
            if (tilt < _stickDeadzone) move = Vector2.zero;
            else if (tilt < 1f) move *= Mathf.InverseLerp(_stickDeadzone, 1f, tilt) / tilt;

            Vector3 forward = _cameraTransform != null ? Flatten(_cameraTransform.forward) : Vector3.forward;
            Vector3 right = _cameraTransform != null ? Flatten(_cameraTransform.right) : Vector3.right;

            Vector3 direction = right * move.x + forward * move.y;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            Vector3 target = direction * _maxSpeed;
            float t = 1f - Mathf.Exp(-_inputSharpness * Time.deltaTime);
            _desiredVelocity = Vector3.Lerp(_desiredVelocity, target, t);
        }

        #endregion

        #region Tools and Utilities

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : v;
        }

        #endregion
    }
}
