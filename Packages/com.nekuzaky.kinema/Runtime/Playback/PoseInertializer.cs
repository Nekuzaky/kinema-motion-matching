using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Inertialization node: instead of crossfading two clips, the graph hard-switches to the new
    /// clip and this job carries the pose discontinuity as a per-bone offset (position + rotation,
    /// with initial velocity) that decays to zero over the blend time using a cubic Hermite curve
    /// (value and velocity both reach zero). This is the post-transition inertialization popularized
    /// by Gears of War 4 (Bollo, GDC 2018) and standard in AAA motion matching.
    ///
    /// The job keeps a two-frame history of its own output, so the offset is captured against what
    /// was actually on screen - including any still-decaying previous transition.
    ///
    /// The offset's initial velocity is the <em>difference</em> between the outgoing and incoming
    /// motion, and on the frame of the switch only one sample of the incoming clip exists. So a
    /// transition spends that frame extrapolating the outgoing pose along its own velocity (nothing
    /// on screen changes course), measures the incoming clip's velocity from two real samples on the
    /// next, and only then captures the offset. Position and velocity are both continuous across the
    /// seam. The maths lives in <see cref="InertializationMath"/>, where it is testable without a rig.
    /// </summary>
    public sealed class PoseInertializer : System.IDisposable
    {
        #region Private and Protected

        private NativeArray<TransformStreamHandle> _handles;
        private NativeArray<InertializationMath.Bone> _bones;
        private NativeArray<InertializationMath.Clock> _clock;
        private AnimationScriptPlayable _playable;
        private bool _created;

        #endregion

        #region Public

        public AnimationScriptPlayable Playable => _playable;
        public bool IsCreated => _created;

        #endregion

        #region Main API

        /// <summary>Creates the script playable and binds every transform under the animator root.</summary>
        public AnimationScriptPlayable Create(PlayableGraph graph, Animator animator)
        {
            Transform root = animator.transform;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            int count = all.Length - 1; // exclude the root itself; root motion is handled by the animator.

            _handles = new NativeArray<TransformStreamHandle>(count, Allocator.Persistent);
            _bones = new NativeArray<InertializationMath.Bone>(count, Allocator.Persistent);
            _clock = new NativeArray<InertializationMath.Clock>(1, Allocator.Persistent);
            _clock[0] = InertializationMath.Clock.Create();

            int h = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == root) continue;
                _handles[h++] = animator.BindStreamTransform(all[i]);
            }

            var job = new InertializationJob { Handles = _handles, Bones = _bones, Clock = _clock };

            _playable = AnimationScriptPlayable.Create(graph, job, 1);
            _playable.SetProcessInputs(true);
            _created = true;
            return _playable;
        }

        /// <summary>Captures the current discontinuity on the next evaluation and decays it over <paramref name="duration"/>.</summary>
        public void RequestTransition(float duration)
        {
            if (!_created) return;
            InertializationMath.Clock clock = _clock[0];
            clock.Request(duration);
            _clock[0] = clock;
        }

        public void Dispose()
        {
            if (!_created) return;
            _created = false;
            _handles.Dispose();
            _bones.Dispose();
            _clock.Dispose();
        }

        #endregion

        #region Tools and Utilities

        [BurstCompile]
        private struct InertializationJob : IAnimationJob
        {
            public NativeArray<TransformStreamHandle> Handles;
            public NativeArray<InertializationMath.Bone> Bones;
            public NativeArray<InertializationMath.Clock> Clock;

            public void ProcessRootMotion(AnimationStream stream) { }

            public void ProcessAnimation(AnimationStream stream)
            {
                InertializationMath.Clock clock = Clock[0];
                InertializationMath.Frame frame = InertializationMath.BeginFrame(ref clock, stream.deltaTime);

                for (int i = 0; i < Handles.Length; i++)
                {
                    TransformStreamHandle handle = Handles[i];
                    InertializationMath.Bone bone = Bones[i];

                    if (InertializationMath.Process(frame, ref bone,
                            handle.GetLocalPosition(stream), handle.GetLocalRotation(stream),
                            out Vector3 outPos, out Quaternion outRot))
                    {
                        handle.SetLocalPosition(stream, outPos);
                        handle.SetLocalRotation(stream, outRot);
                    }

                    Bones[i] = bone;
                }

                InertializationMath.EndFrame(ref clock, frame);
                Clock[0] = clock;
            }
        }

        #endregion
    }

    /// <summary>
    /// The per-bone maths of <see cref="PoseInertializer"/>, kept free of animation streams so it is
    /// testable on its own. Drive it the way the job does: <see cref="BeginFrame"/>, then
    /// <see cref="Process"/> for every bone, then <see cref="EndFrame"/>.
    ///
    /// Rotation offsets are axis * angle (radians) applied on the left, in the bone's parent space:
    /// <c>out = FromAxisAngle(offset) * in</c>.
    /// </summary>
    public static class InertializationMath
    {
        #region Public

        /// <summary>Shorter steps than this measure no velocity (debug scrubs, a paused game).</summary>
        public const float MinDeltaTime = 1e-4f;

        /// <summary>Per-bone state: two frames of output history plus the live offset.</summary>
        public struct Bone
        {
            public Vector3 PrevPos, PrevPrevPos;
            public Quaternion PrevRot, PrevPrevRot;
            public Vector3 OffsetPos, OffsetPosVel;
            public Vector3 OffsetRot, OffsetRotVel;

            // Held across the one frame a transition spends measuring the incoming clip.
            public Vector3 OutgoingVel, OutgoingAngVel, IncomingPos;
            public Quaternion IncomingRot;
        }

        /// <summary>Whole-pose state, shared by every bone.</summary>
        public struct Clock
        {
            public float Elapsed;
            public float Duration;
            /// <summary>Delta time of the previous evaluation - what the output history was measured over.</summary>
            public float PrevDeltaTime;
            /// <summary>Evaluations seen, saturating at 2 (two are needed to know a velocity).</summary>
            public int History;
            public int Requested;
            public int AwaitingIncoming;

            public static Clock Create() => new Clock { Duration = 1f, Elapsed = 1f };

            public void Request(float duration)
            {
                Duration = Mathf.Max(0.01f, duration);
                Requested = 1;
            }
        }

        /// <summary>What this evaluation does, decided once for all bones by <see cref="BeginFrame"/>.</summary>
        public struct Frame
        {
            public float DeltaTime;
            /// <summary>A transition starts this frame: record the outgoing velocity, extrapolate.</summary>
            public bool Begin;
            /// <summary>Still waiting for a real step to measure the incoming clip: keep extrapolating.</summary>
            public bool Extrapolate;
            /// <summary>The incoming velocity is measurable now: capture the offset.</summary>
            public bool Capture;
            /// <summary>An offset is decaying (or was just captured): apply it.</summary>
            public bool Apply;
            public float H00, H10, Duration;
            /// <summary>The previous step was long enough for the output history to carry a velocity.</summary>
            public bool HistoryHasVelocity;
            public float PrevDeltaTime;
        }

        public static Frame BeginFrame(ref Clock clock, float deltaTime)
        {
            var frame = new Frame
            {
                DeltaTime = deltaTime,
                PrevDeltaTime = clock.PrevDeltaTime,
                HistoryHasVelocity = clock.PrevDeltaTime >= MinDeltaTime
            };
            bool realStep = deltaTime >= MinDeltaTime;

            // A transition requested since the last evaluation. Without two frames of history there
            // is no outgoing motion to carry: hard cut.
            if (clock.Requested != 0)
            {
                clock.Requested = 0;
                if (clock.History >= 2)
                {
                    frame.Begin = true;
                    clock.AwaitingIncoming = 1;
                }
                else
                {
                    clock.AwaitingIncoming = 0;
                    clock.Elapsed = clock.Duration;
                }
            }

            if (!frame.Begin && clock.AwaitingIncoming != 0 && realStep)
            {
                frame.Capture = true;
                clock.AwaitingIncoming = 0;
                clock.Elapsed = 0f;
            }
            frame.Extrapolate = clock.AwaitingIncoming != 0;

            frame.Duration = clock.Duration;
            bool decaying = clock.Elapsed < clock.Duration;
            float s = decaying ? Mathf.Clamp01(clock.Elapsed / clock.Duration) : 1f;
            frame.H00 = HermitePosition(s);
            frame.H10 = HermiteVelocity(s);
            frame.Apply = !frame.Extrapolate && (decaying || frame.Capture);
            return frame;
        }

        /// <summary>
        /// One bone, one evaluation. Returns true when the output differs from the input (the
        /// caller writes it back); the history is updated either way.
        /// </summary>
        public static bool Process(in Frame frame, ref Bone bone, Vector3 inPos, Quaternion inRot,
                                   out Vector3 outPos, out Quaternion outRot)
        {
            float dt = frame.DeltaTime >= MinDeltaTime ? frame.DeltaTime : 0f;
            outPos = inPos;
            outRot = inRot;

            if (frame.Begin)
            {
                bone.OutgoingVel = frame.HistoryHasVelocity
                    ? (bone.PrevPos - bone.PrevPrevPos) / frame.PrevDeltaTime
                    : Vector3.zero;
                bone.OutgoingAngVel = frame.HistoryHasVelocity
                    ? AngularVelocity(bone.PrevPrevRot, bone.PrevRot, frame.PrevDeltaTime)
                    : Vector3.zero;
            }

            if (frame.Extrapolate)
            {
                // The outgoing pose keeps its own course while the incoming clip is measured. The
                // incoming sample to measure from is always the latest one: a zero-length step in
                // between (a paused game) must not become the baseline of a velocity.
                outPos = bone.PrevPos + bone.OutgoingVel * dt;
                outRot = Integrate(bone.PrevRot, bone.OutgoingAngVel, dt);
                bone.IncomingPos = inPos;
                bone.IncomingRot = inRot;
            }
            else
            {
                if (frame.Capture)
                {
                    Vector3 incomingVel = (inPos - bone.IncomingPos) / dt;
                    Vector3 incomingAngVel = AngularVelocity(bone.IncomingRot, inRot, dt);
                    Vector3 sourcePos = bone.PrevPos + bone.OutgoingVel * dt;
                    Quaternion sourceRot = Integrate(bone.PrevRot, bone.OutgoingAngVel, dt);

                    bone.OffsetPos = sourcePos - inPos;
                    bone.OffsetPosVel = bone.OutgoingVel - incomingVel;
                    bone.OffsetRot = ToAxisAngle(sourceRot * Conjugate(inRot));
                    bone.OffsetRotVel = bone.OutgoingAngVel - incomingAngVel;
                }

                if (frame.Apply)
                {
                    Vector3 pOff = frame.H00 * bone.OffsetPos + frame.H10 * frame.Duration * bone.OffsetPosVel;
                    Vector3 rOff = frame.H00 * bone.OffsetRot + frame.H10 * frame.Duration * bone.OffsetRotVel;
                    outPos = inPos + pOff;
                    outRot = FromAxisAngle(rOff) * inRot;
                }
            }

            bone.PrevPrevPos = bone.PrevPos;
            bone.PrevPrevRot = bone.PrevRot;
            bone.PrevPos = outPos;
            bone.PrevRot = outRot;
            return frame.Extrapolate || frame.Apply;
        }

        public static void EndFrame(ref Clock clock, in Frame frame)
        {
            // The decay clock only runs once the offset exists.
            if (!frame.Extrapolate) clock.Elapsed += Mathf.Max(frame.DeltaTime, 0f);
            clock.PrevDeltaTime = frame.DeltaTime;
            if (clock.History < 2) clock.History++;
        }

        /// <summary>Cubic Hermite basis h00: 1 at s=0, 0 at s=1, flat at both ends.</summary>
        public static float HermitePosition(float s)
        {
            float s2 = s * s, s3 = s2 * s;
            return 2f * s3 - 3f * s2 + 1f;
        }

        /// <summary>Cubic Hermite basis h10: slope 1 at s=0, value and slope 0 at s=1.</summary>
        public static float HermiteVelocity(float s)
        {
            float s2 = s * s, s3 = s2 * s;
            return s3 - 2f * s2 + s;
        }

        /// <summary>Angular velocity (parent space, rad/s) that turns <paramref name="from"/> into <paramref name="to"/> over <paramref name="dt"/>.</summary>
        public static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
        {
            return dt > 0f ? ToAxisAngle(to * Conjugate(from)) / dt : Vector3.zero;
        }

        /// <summary><paramref name="rotation"/> advanced by <paramref name="angularVelocity"/> for <paramref name="dt"/> seconds.</summary>
        public static Quaternion Integrate(Quaternion rotation, Vector3 angularVelocity, float dt)
        {
            return FromAxisAngle(angularVelocity * dt) * rotation;
        }

        /// <summary>Inverse of a unit quaternion.</summary>
        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        /// <summary>Shortest-arc axis * angle of a unit quaternion.</summary>
        public static Vector3 ToAxisAngle(Quaternion q)
        {
            if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
            float w = Mathf.Clamp(q.w, -1f, 1f);
            float angle = 2f * Mathf.Acos(w);
            float s = Mathf.Sqrt(Mathf.Max(1f - w * w, 0f));
            if (s < 1e-6f) return Vector3.zero;
            return new Vector3(q.x / s, q.y / s, q.z / s) * angle;
        }

        public static Quaternion FromAxisAngle(Vector3 axisAngle)
        {
            float angle = axisAngle.magnitude;
            if (angle < 1e-6f) return Quaternion.identity;
            Vector3 axis = axisAngle / angle;
            float half = angle * 0.5f;
            float sin = Mathf.Sin(half);
            return new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, Mathf.Cos(half));
        }

        #endregion
    }
}
