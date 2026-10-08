using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Orbit-follow camera for the demo: eases toward a shoulder-height orbit around the target,
    /// with the right stick (or middle-mouse drag) steering yaw and pitch. Without input it behaves
    /// exactly like the old fixed-offset follow, so the demo still reads as "camera stays out of
    /// the way" until you reach for the stick.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Follow Camera")]
    public sealed class FollowCamera : MonoBehaviour
    {
        #region Public

        [SerializeField] private Transform _target;
        [SerializeField] private float _lookHeight = 1.2f;
        [SerializeField, Min(0f)] private float _followSharpness = 6f;

        [Tooltip("How quickly the camera follows the character's height. Lower than the follow " +
                 "sharpness so a stair step - which the character controller climbs in one frame - " +
                 "reads as a glide instead of a jolt of the whole view.")]
        [SerializeField, Min(0f)] private float _heightSharpness = 5f;

        [Header("Orbit")]
        [SerializeField, Min(1f)] private float _distance = 6.7f;
        [SerializeField, Range(-30f, 75f)] private float _pitch = 24f;
        [SerializeField, Min(10f)] private float _orbitSpeed = 140f;
        [SerializeField, Range(-25f, 70f)] private float _minPitch = -5f;
        [SerializeField, Range(-25f, 80f)] private float _maxPitch = 65f;

        public void SetTarget(Transform target) => _target = target;

        #endregion

        #region Private and Protected

        private DemoVector _orbitAction;
        private float _yaw;
        private Vector3 _focus;
        private bool _focusPrimed;

        #endregion

        #region Unity API

        private void Awake()
        {
            // Right stick, or mouse delta while the middle button is held (plain mouse movement stays free).
            _orbitAction = DemoInput.Orbit();
        }

        private void OnEnable() => _orbitAction.Enable();
        private void OnDisable() => _orbitAction.Disable();

        private void Start()
        {
            // Safety net: if the serialized reference was lost, follow the matched character.
            if (_target == null)
            {
                var controller = UnityCompat.FindFirst<MotionMatchingController>();
                if (controller != null) _target = controller.transform;
            }
            _yaw = transform.eulerAngles.y;
        }

        private void LateUpdate()
        {
            if (_target == null) return;

            Vector2 orbit = _orbitAction.ReadValue();
            // Mouse deltas arrive in pixels, sticks in [-1,1]; the clamp keeps a fast mouse from spinning the rig.
            orbit.x = Mathf.Clamp(orbit.x, -1.5f, 1.5f);
            orbit.y = Mathf.Clamp(orbit.y, -1.5f, 1.5f);

            _yaw += orbit.x * _orbitSpeed * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch - orbit.y * _orbitSpeed * 0.6f * Time.deltaTime, _minPitch, _maxPitch);

            // Aim at a focus point that follows the root exactly across the ground but eases in
            // height. Position was already smoothed; the aim was not - LookAt took the raw root, so
            // every step the controller climbed (0.16 m in one frame) jerked the whole view with it.
            Vector3 root = _target.position;
            if (!_focusPrimed) { _focus = root; _focusPrimed = true; }
            float th = 1f - Mathf.Exp(-_heightSharpness * Time.deltaTime);
            _focus = new Vector3(root.x, Mathf.Lerp(_focus.y, root.y, th), root.z);

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 desired = _focus + rotation * new Vector3(0f, 0f, -_distance);

            float t = 1f - Mathf.Exp(-_followSharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, t);
            transform.LookAt(_focus + Vector3.up * _lookHeight);
        }

        #endregion
    }
}
