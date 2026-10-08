using System;
using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Describes the layout of a feature vector: how many trajectory points, which bones,
    /// and where every group lives inside the flat float array. A schema is authored on the
    /// <see cref="MotionMatchingConfig"/> and snapshotted into the <see cref="MotionMatchingDatabase"/>
    /// at bake time, so a database always knows exactly how to interpret its own numbers even if
    /// the config later changes.
    ///
    /// Layout (contiguous groups, in order):
    ///   [ TrajectoryPosition 2*T | TrajectoryDirection 2*T | BonePosition 3*B | BoneVelocity 3*B | RootVelocity 2 ]
    ///
    /// In <see cref="PoseCostMode.InertializationCost"/> the BoneVelocity group is empty and the
    /// BonePosition group carries the composite instead - see <see cref="PoseCostMode"/>.
    /// </summary>
    [Serializable]
    public sealed class FeatureSchema
    {
        #region Public

        [Tooltip("How the pose half of the vector is built. Inertialization Cost is smaller and needs " +
                 "no velocity weight; Naive is the classic layout. Changing this needs a rebake.")]
        public PoseCostMode PoseMode = PoseCostMode.Naive;

        [Tooltip("Half-life (seconds) of the inertializer the pose cost assumes. Only used by " +
                 "Inertialization Cost. Match your transition half-life; 0.15 is Holden's demo value.")]
        public float InertializationHalflife = 0.15f;

        [Tooltip("Trajectory time offsets (seconds). Negative = past (from history), positive = future (predicted). e.g. -0.4, -0.2, 0.2, 0.4, 0.6, 1.0.")]
        public float[] TrajectoryTimes = { -0.4f, -0.2f, 0.2f, 0.4f, 0.6f, 1.0f };

        [Tooltip("Transform names sampled for the pose, matched against the rig hierarchy (e.g. LeftFoot, RightFoot, Hips).")]
        public string[] BoneNames = { "LeftFoot", "RightFoot", "Hips" };

        [Tooltip("Per-bone importance multiplier, parallel to Bone Names. Raise feet for crisper footing (MxM-style joint weights).")]
        public float[] BoneWeights = { 1f, 1f, 1f };

        #endregion

        #region Tools and Utilities

        public int TrajectoryPointCount => TrajectoryTimes?.Length ?? 0;
        public int BoneCount => BoneNames?.Length ?? 0;

        public int TrajectoryPositionOffset => 0;
        public int TrajectoryDirectionOffset => TrajectoryPositionOffset + TrajectoryPointCount * 2;
        public int BonePositionOffset => TrajectoryDirectionOffset + TrajectoryPointCount * 2;
        public int BoneVelocityOffset => BonePositionOffset + BoneCount * 3;

        // Under InertializationCost the velocity group is empty - position and velocity are folded
        // into one composite living in the BonePosition group - so every later offset slides down by
        // 3*B and the whole layout falls out of this one line.
        public int RootVelocityOffset =>
            BoneVelocityOffset + (PoseMode == PoseCostMode.Naive ? BoneCount * 3 : 0);

        /// <summary>
        /// Half-damping <c>y</c> of the assumed inertializer, from the half-life. This exact
        /// conversion (Holden, "Spring-It-On": <c>damping = 4*ln2/halflife</c>, <c>y = damping/2</c>)
        /// is what makes the composite mean anything - it has to be the same number the spring the
        /// transition actually runs would use.
        /// </summary>
        public float HalfDamping => 2f * 0.69314718056f / Mathf.Max(1e-5f, InertializationHalflife);

        /// <summary>Total number of float dimensions in one feature vector.</summary>
        public int Dimension => RootVelocityOffset + 2;

        /// <summary>
        /// The pose value written for one bone, in whichever mode this schema is in. The single
        /// definition on purpose: the baker and the live query must produce bit-identical numbers or
        /// every cost in the system is measured against a different yardstick than it was baked
        /// with, and nothing about that failure would look like a formula mismatch.
        ///
        /// Under <see cref="PoseCostMode.InertializationCost"/> this is Holden's
        /// <c>2*pos/y + vel/y^2</c>: the displacement an inertialized transition onto this bone
        /// would actually cause. Position and velocity stop being two things to weigh against each
        /// other and become one number that already knows a position offset is harmless when the
        /// velocity offset is set to cancel it.
        /// </summary>
        public Vector3 BonePoseValue(Vector3 position, Vector3 velocity)
        {
            if (PoseMode == PoseCostMode.Naive) return position;
            float y = HalfDamping;
            return 2f * position / y + velocity / (y * y);
        }

        public int GetGroupOffset(FeatureGroup group)
        {
            switch (group)
            {
                case FeatureGroup.TrajectoryPosition: return TrajectoryPositionOffset;
                case FeatureGroup.TrajectoryDirection: return TrajectoryDirectionOffset;
                case FeatureGroup.BonePosition: return BonePositionOffset;
                case FeatureGroup.BoneVelocity: return BoneVelocityOffset;
                case FeatureGroup.RootVelocity: return RootVelocityOffset;
                default: return 0;
            }
        }

        public int GetGroupLength(FeatureGroup group)
        {
            switch (group)
            {
                case FeatureGroup.TrajectoryPosition: return TrajectoryPointCount * 2;
                case FeatureGroup.TrajectoryDirection: return TrajectoryPointCount * 2;
                case FeatureGroup.BonePosition: return BoneCount * 3;
                case FeatureGroup.BoneVelocity: return PoseMode == PoseCostMode.Naive ? BoneCount * 3 : 0;
                case FeatureGroup.RootVelocity: return 2;
                default: return 0;
            }
        }

        /// <summary>Returns which group the given flat dimension belongs to.</summary>
        public FeatureGroup GetGroupOf(int dimension)
        {
            if (dimension < TrajectoryDirectionOffset) return FeatureGroup.TrajectoryPosition;
            if (dimension < BonePositionOffset) return FeatureGroup.TrajectoryDirection;
            if (dimension < BoneVelocityOffset) return FeatureGroup.BonePosition;
            if (dimension < RootVelocityOffset) return FeatureGroup.BoneVelocity;
            return FeatureGroup.RootVelocity;
        }

        /// <summary>
        /// Expands the per-group weights into a per-dimension weight array, ready for the inner
        /// distance loop. Built once and cached by the matcher.
        /// </summary>
        public float[] BuildPerDimensionWeights(FeatureWeights weights)
        {
            var result = new float[Dimension];
            BuildPerDimensionWeights(weights, result);
            return result;
        }

        /// <summary>
        /// <see cref="BuildPerDimensionWeights(FeatureWeights)"/> into an existing array of length
        /// <see cref="Dimension"/>, so a live weight change (tuning, a calibration profile) allocates
        /// nothing.
        /// </summary>
        public void BuildPerDimensionWeights(FeatureWeights weights, float[] result)
        {
            for (int gi = 0; gi < FeatureGroupExtensions.Count; gi++)
            {
                var group = (FeatureGroup)gi;
                int offset = GetGroupOffset(group);
                int length = GetGroupLength(group);
                float w = Mathf.Max(0f, weights.Get(group));

                // Bone groups additionally scale by the per-bone weight (3 dims per bone).
                bool perBone = group == FeatureGroup.BonePosition || group == FeatureGroup.BoneVelocity;
                for (int i = 0; i < length; i++)
                    result[offset + i] = perBone ? w * GetBoneWeight(i / 3) : w;
            }
        }

        /// <summary>Per-bone multiplier; missing entries default to 1.</summary>
        public float GetBoneWeight(int boneIndex)
        {
            if (BoneWeights == null || boneIndex < 0 || boneIndex >= BoneWeights.Length) return 1f;
            return Mathf.Max(0f, BoneWeights[boneIndex]);
        }

        public FeatureSchema Clone()
        {
            return new FeatureSchema
            {
                TrajectoryTimes = (float[])(TrajectoryTimes?.Clone() ?? Array.Empty<float>()),
                BoneNames = (string[])(BoneNames?.Clone() ?? Array.Empty<string>()),
                BoneWeights = (float[])(BoneWeights?.Clone() ?? Array.Empty<float>()),
                PoseMode = PoseMode,
                InertializationHalflife = InertializationHalflife
            };
        }

        /// <summary>
        /// True when both schemas share the exact same dimension layout. The pose mode counts:
        /// two schemas can agree on every count and still write different numbers into the same
        /// slots, which is the one incompatibility that would otherwise pass silently.
        /// </summary>
        public bool IsLayoutCompatibleWith(FeatureSchema other)
        {
            if (other == null) return false;
            return TrajectoryPointCount == other.TrajectoryPointCount
                   && BoneCount == other.BoneCount
                   && PoseMode == other.PoseMode
                   && (PoseMode == PoseCostMode.Naive
                       || Mathf.Approximately(InertializationHalflife, other.InertializationHalflife));
        }

        #endregion
    }
}
