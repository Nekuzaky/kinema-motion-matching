using Kinema.MotionMatching;
using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Demo vault trigger, MxM-style event workflow: a chest-height ray probes ahead for a low
    /// obstacle; when one is in range and the player presses jump, the controller plays the vault
    /// motion event with its contact warped onto the obstacle's near edge. Matching pauses for the
    /// clip and resumes on landing; the inertializer absorbs both seams.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Vault Trigger")]
    [RequireComponent(typeof(MotionMatchingController))]
    public sealed class VaultTrigger : MonoBehaviour
    {
        #region Public

        [Tooltip("The vault motion event (clip + contact time + warp settings).")]
        [SerializeField] private MotionEventDefinition _vaultEvent;

        [Tooltip("Free jump while moving, when no obstacle is ahead. Plays with its own root motion, no warp.")]
        [SerializeField] private MotionEventDefinition _jumpMovingEvent;

        [Tooltip("Free jump from standstill, when no obstacle is ahead.")]
        [SerializeField] private MotionEventDefinition _jumpIdleEvent;

        [Tooltip("How close the obstacle must be to trigger (meters). What counts as vaultable at all " +
                 "is the Obstacle Sensor's call - height, thickness and landing live there.")]
        [SerializeField, Range(0.5f, 3f)] private float _maxDistance = 1.4f;

        [Tooltip("Vault automatically when eligible and moving toward the obstacle - for AI characters or hands-free traversal.")]
        [SerializeField] private bool _autoVault;

        /// <summary>True while an eligible obstacle is ahead (drive a UI prompt from this).</summary>
        public bool CanVault { get; private set; }

        #endregion

        #region Private and Protected

        private MotionMatchingController _controller;
        private ObstacleSensor _sensor;
        private DemoButton _vaultAction;

        #endregion

        #region Unity API

        private void Awake()
        {
            _controller = GetComponent<MotionMatchingController>();
            _sensor = GetComponent<ObstacleSensor>();
            if (_sensor == null)
                KinemaLog.Misconfigured($"'{name}': no ObstacleSensor beside the VaultTrigger, so " +
                                        "nothing reads as vaultable and Space only free-jumps.", this);

            _vaultAction = DemoInput.Button("Vault",
                new[] { "<Keyboard>/space", "<Gamepad>/buttonSouth" }, KeyCode.Space, KeyCode.JoystickButton0);
        }

        private void OnEnable() => _vaultAction.Enable();
        private void OnDisable() => _vaultAction.Disable();

        private void Update()
        {
            CanVault = false;
            if (!_controller.IsInitialized || _controller.IsPlayingEvent) return;

            // The sensor decides what is ahead; this only decides whether to act on it. Its reading
            // is shared and already throttled, so asking costs nothing, and it is a strictly better
            // answer than the ray this used to cast: it has measured the obstacle's thickness and
            // checked there is floor beyond to arrive on. Without those a vault would warp the
            // character into the middle of a deep block, or over a wall into a pit.
            ObstacleReading ahead = _sensor != null ? _sensor.Reading : default;

            if (_vaultEvent != null && ahead.Kind == ObstacleKind.Vault && ahead.Distance <= _maxDistance)
            {
                CanVault = true;

                // AI path: no input needed - vault as soon as the character is heading into the obstacle.
                bool autoTriggered = _autoVault && Vector3.Dot(_controller.DesiredVelocity, Flatten(transform.forward)) > 0.3f;

                if (autoTriggered || _vaultAction.WasPressedThisFrame())
                {
                    if (KinemaLog.Verbose)
                        KinemaLog.Event($"{name}: vault over '{(ahead.Collider != null ? ahead.Collider.name : "?")}' " +
                                        $"(top {ahead.Height:F2} m, {ahead.Depth:F2} m thick)", this);
                    Vault(ahead);
                }
                return;
            }

            // Open ground: the same button is a free jump. The event plays unwarped, so the arc is
            // the clip's own root motion; the motor already trusts vertical root motion during events.
            if (_vaultAction.WasPressedThisFrame())
            {
                bool moving = _controller.DesiredVelocity.sqrMagnitude > 0.25f;
                MotionEventDefinition jump = moving ? _jumpMovingEvent : _jumpIdleEvent;
                if (jump == null) jump = moving ? _jumpIdleEvent : _jumpMovingEvent;
                if (jump != null)
                {
                    if (KinemaLog.Verbose)
                        KinemaLog.Event($"{name}: free jump ({(moving ? "moving" : "standing")}) with '{jump.name}'", this);
                    _controller.PlayEvent(jump, transform.position, Quaternion.LookRotation(Flatten(transform.forward), Vector3.up));
                }
                else
                {
                    // Always, not verbose: a button that does nothing is the thing you are trying to
                    // explain, and it needs no repro.
                    KinemaLog.Misconfigured($"'{name}': jump pressed, but no obstacle in the vault " +
                                            "window and no free-jump event bound.", this);
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = CanVault ? Color.green : Color.gray;
            Vector3 origin = transform.position + Vector3.up * 0.6f;
            Gizmos.DrawLine(origin, origin + Flatten(transform.forward) * _maxDistance);
            // The sensor draws what it found; this only draws how close it must be to act.
        }

        #endregion

        #region Tools and Utilities

        private void Vault(ObstacleReading ahead)
        {
            Vector3 forward = Flatten(transform.forward);

            // Contact lands just past the near edge; height comes from the clip's root arc.
            Vector3 contact = ahead.Point + forward * 0.25f;
            contact.y = transform.position.y;

            _controller.PlayEvent(_vaultEvent, contact, Quaternion.LookRotation(forward, Vector3.up));
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        #endregion
    }
}
