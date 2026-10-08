using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Conforms the feet to whatever they are actually standing on. Baked clips were authored on a
    /// flat floor, so on a ramp or a step the animation keeps the feet at the clip's height: they
    /// float above the surface or sink into it. This raycasts under each foot, plants it on the real
    /// surface, tilts it to the slope, and drops the pelvis so the lower foot can still reach - the
    /// same order of operations every AAA locomotion rig uses.
    ///
    /// Runs late (after the graph and after <see cref="FootLockIK"/>, whose horizontal pinning it
    /// complements: this component only touches height and foot orientation).
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Ground Adaptation IK")]
    [RequireComponent(typeof(MotionMatchingController))]
    [DefaultExecutionOrder(200)]
    public sealed class GroundAdaptationIK : MonoBehaviour
    {
        #region Public

        [SerializeField, Range(0f, 1f)] private float _weight = 1f;

        [Tooltip("Layers treated as ground.")]
        [SerializeField] private LayerMask _groundLayers = ~0;

        [Tooltip("How far above the foot the probe starts (meters).")]
        [SerializeField, Range(0.1f, 1f)] private float _probeHeight = 0.5f;

        [Tooltip("How far below the foot the probe reaches (meters).")]
        [SerializeField, Range(0.1f, 1f)] private float _probeDistance = 0.6f;

        [Tooltip("Distance from the ankle to the sole (meters).")]
        [SerializeField, Range(0f, 0.3f)] private float _ankleHeight = 0.11f;

        [Tooltip("Largest pelvis drop allowed to reach a lower foot (meters).")]
        [SerializeField, Range(0f, 0.6f)] private float _maxPelvisDrop = 0.35f;

        [Tooltip("Smoothing for foot and pelvis height. Higher = snappier, lower = floatier.")]
        [SerializeField, Range(1f, 30f)] private float _sharpness = 12f;

        [Tooltip("Do not tilt the foot to slopes steeper than this (degrees).")]
        [SerializeField, Range(10f, 70f)] private float _maxSlopeAngle = 45f;

        public float Weight { get => _weight; set => _weight = Mathf.Clamp01(value); }

        #endregion

        #region Private and Protected

        private struct Leg
        {
            public Transform Upper, Lower, Foot;
            public float HeightOffset;   // smoothed vertical correction
            public Quaternion Tilt;      // smoothed sole alignment
            public bool Valid;
        }

        private MotionMatchingController _controller;
        private Transform _pelvis;
        private Leg[] _legs;
        private float _pelvisOffset;
        private float _heldPelvisTarget;
        private float _airborneTime;
        private bool _resolved;

        // A run's flight phase, or a walk between one foot lifting and the other planting, is a
        // fraction of this. Longer than it, the character is genuinely off the ground.
        private const float MaxContactGap = 0.3f;

        #endregion

        #region Unity API

        private void Awake() => _controller = GetComponent<MotionMatchingController>();

        private void LateUpdate()
        {
            if (_weight <= 0f || _controller == null || !_controller.IsInitialized) return;

            MotionMatchingDatabase db = _controller.Database;
            if (db == null || db.ContactBoneCount == 0) return;
            if (!_resolved) Resolve(db);
            if (_legs == null || _pelvis == null) return; // Resolve can no-op mid-reinit (spawned ghost).

            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            float t = 1f - Mathf.Exp(-_sharpness * dt);

            // Which feet are actually down this frame. Everything below hangs on it: a foot in the
            // air is not standing on anything, so it has no surface to be conformed to and no say in
            // where the pelvis goes. Correcting it anyway pins it to the floor for the whole swing -
            // the character stops picking its feet up, and drags the pelvis down to wherever the
            // swing foot happened to be. That reads as walking while seated.
            byte contacts = _controller.CurrentContacts;

            // Pass 1: probe every planted foot, find how much each needs to move vertically.
            float lowestCorrection = 0f;
            bool anyGrounded = false;
            for (int i = 0; i < _legs.Length; i++)
            {
                if (!_legs[i].Valid) continue;

                bool grounded = (contacts & (1 << i)) != 0;
                ProbeLeg(ref _legs[i], t, grounded);
                if (!grounded) continue;
                anyGrounded = true;
                lowestCorrection = Mathf.Min(lowestCorrection, _legs[i].HeightOffset);
            }

            // Pass 2: a planted foot that must drop below the clip's floor can only reach if the
            // pelvis follows it down; otherwise the leg would have to stretch.
            //
            // With no foot down, hold the last answer through a short gap. Treating "no planted foot"
            // as "no correction" sent the pelvis back up during every run flight phase and dropped it
            // again on the next plant: on stairs and slopes the whole body bobbed once per step.
            // Only a gap too long to be a stride (a jump, a vault) lets it return to the clip.
            if (anyGrounded)
            {
                _airborneTime = 0f;
                _heldPelvisTarget = Mathf.Clamp(lowestCorrection, -_maxPelvisDrop, 0f);
            }
            else
            {
                _airborneTime += dt;
                if (_airborneTime > MaxContactGap || _controller.IsPlayingEvent) _heldPelvisTarget = 0f;
            }
            float pelvisTarget = _heldPelvisTarget * _weight;
            _pelvisOffset = Mathf.Lerp(_pelvisOffset, pelvisTarget, t);
            _pelvis.position += Vector3.up * _pelvisOffset;

            // Pass 3: solve each foot onto its surface, now that the pelvis has settled.
            for (int i = 0; i < _legs.Length; i++)
            {
                if (!_legs[i].Valid) continue;
                ApplyLeg(_legs[i]);
            }
        }

        #endregion

        #region Tools and Utilities

        private void Resolve(MotionMatchingDatabase db)
        {
            _resolved = true;
            _legs = new Leg[db.ContactBoneCount];

            for (int i = 0; i < _legs.Length; i++)
            {
                Transform foot = FindDeep(transform, db.GetContactBoneName(i));
                if (foot == null || foot.parent == null || foot.parent.parent == null) continue;

                _legs[i] = new Leg
                {
                    Foot = foot,
                    Lower = foot.parent,
                    Upper = foot.parent.parent,
                    Tilt = Quaternion.identity,
                    Valid = true
                };
                // The pelvis is the common ancestor above both hips.
                if (_pelvis == null && foot.parent.parent.parent != null)
                    _pelvis = foot.parent.parent.parent;
            }
        }

        /// <summary>
        /// Vertical correction and sole tilt for one leg. A foot that is not planted gets neither:
        /// its target is zero, so the smoothing carries it back to whatever arc the clip authored.
        /// The alternative - measuring a swing foot against the floor below it - reports the whole
        /// height of the step as an error to be corrected away, which is the one thing that must not
        /// happen to a foot whose entire job this frame is to be off the ground.
        /// </summary>
        private void ProbeLeg(ref Leg leg, float t, bool grounded)
        {
            Vector3 footPosition = leg.Foot.position;
            Vector3 origin = footPosition + Vector3.up * _probeHeight;
            float targetOffset = 0f;
            Quaternion targetTilt = Quaternion.identity;

            if (grounded && Physics.Raycast(origin, Vector3.down, out RaycastHit hit, _probeHeight + _probeDistance, _groundLayers, QueryTriggerInteraction.Ignore))
            {
                float soleY = footPosition.y - _ankleHeight;
                targetOffset = hit.point.y - soleY;

                float slope = Vector3.Angle(hit.normal, Vector3.up);
                if (slope <= _maxSlopeAngle)
                    targetTilt = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }

            leg.HeightOffset = Mathf.Lerp(leg.HeightOffset, targetOffset, t);
            leg.Tilt = Quaternion.Slerp(leg.Tilt, targetTilt, t);
        }

        private void ApplyLeg(Leg leg)
        {
            Vector3 target = leg.Foot.position + Vector3.up * (leg.HeightOffset * _weight - _pelvisOffset);
            SolveTwoBone(leg.Upper, leg.Lower, leg.Foot, target);
            leg.Foot.rotation = Quaternion.Slerp(leg.Foot.rotation, leg.Tilt * leg.Foot.rotation, _weight);
        }

        /// <summary>Analytic two-bone IK: set the knee angle by law of cosines, then aim the chain at the target.</summary>
        private static void SolveTwoBone(Transform upper, Transform lower, Transform foot, Vector3 target)
        {
            Vector3 a = upper.position;
            Vector3 b = lower.position;
            Vector3 c = foot.position;

            float lab = (b - a).magnitude;
            float lcb = (c - b).magnitude;
            if (lab < 1e-4f || lcb < 1e-4f) return;
            float lat = Mathf.Clamp((target - a).magnitude, 0.01f, lab + lcb - 1e-3f);

            float acAb = AngleBetween(c - a, b - a);
            float baBc = AngleBetween(a - b, c - b);

            float acAbDesired = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float baBcDesired = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            Vector3 axis = Vector3.Cross(c - a, b - a);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(c - a, Vector3.up);
            if (axis.sqrMagnitude < 1e-8f) return;
            axis.Normalize();

            upper.rotation = Quaternion.AngleAxis((acAbDesired - acAb) * Mathf.Rad2Deg, axis) * upper.rotation;
            lower.rotation = Quaternion.AngleAxis((baBcDesired - baBc) * Mathf.Rad2Deg, axis) * lower.rotation;

            Vector3 swingAxis = Vector3.Cross(foot.position - a, target - a);
            if (swingAxis.sqrMagnitude > 1e-8f)
            {
                float swing = AngleBetween(foot.position - a, target - a);
                upper.rotation = Quaternion.AngleAxis(swing * Mathf.Rad2Deg, swingAxis.normalized) * upper.rotation;
            }
        }

        private static float AngleBetween(Vector3 u, Vector3 v)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(u.normalized, v.normalized), -1f, 1f));
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        #endregion
    }
}
