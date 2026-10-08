using UnityEngine;

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// Sample locomotion motor: turns the animation's root motion into collision-aware movement.
    /// Motion matching decides <em>what pose to play</em>; this decides <em>how the body moves through
    /// the world</em>. Keeping them separate (the AAA pattern) means the matcher never needs to know
    /// about walls, slopes or gravity.
    ///
    /// Root motion is intercepted in <see cref="OnAnimatorMove"/> and resolved through a
    /// <see cref="CharacterController"/>, which handles sliding along colliders and step offsets.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Samples/Character Motor")]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(Animator))]
    public sealed class CharacterMotor : MonoBehaviour
    {
        #region Public

        [Tooltip("Downward acceleration applied when airborne (m/s²).")]
        [SerializeField, Min(0f)] private float _gravity = 20f;

        [Tooltip("Small constant push keeping the controller planted on the ground.")]
        [SerializeField, Min(0f)] private float _groundStick = 2f;

        public bool IsGrounded => _controller != null && _controller.isGrounded;
        public float VerticalVelocity => _verticalVelocity;

        #endregion

        #region Private and Protected

        private CharacterController _controller;
        private Animator _animator;
        private MotionMatchingController _matching;
        private float _verticalVelocity;

        #endregion

        #region Unity API

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _matching = GetComponent<MotionMatchingController>();
            _animator.applyRootMotion = true; // We consume it ourselves in OnAnimatorMove.
        }

        // Called by the Animator after it evaluates; because we implement it, Unity hands us the
        // root motion instead of applying it directly.
        private void OnAnimatorMove()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 delta = _animator.deltaPosition;
            Quaternion turn = _animator.deltaRotation;

            // Mirrored frames and the simulation sync: the controller corrects the step before the
            // collision pass moves the body by it.
            if (_matching != null) _matching.ProcessRootMotion(ref delta, ref turn);

            if (_matching != null && _matching.IsPlayingEvent)
            {
                // Events (vault, climb) author their own vertical arc: trust the clip, not gravity.
                _verticalVelocity = 0f;
            }
            else
            {
                if (_controller.isGrounded && _verticalVelocity <= 0f)
                    _verticalVelocity = -_groundStick;
                else
                    _verticalVelocity -= _gravity * dt;

                delta.y = _verticalVelocity * dt;
            }

            _controller.Move(delta);

            transform.rotation *= turn;
        }

        #endregion
    }
}
