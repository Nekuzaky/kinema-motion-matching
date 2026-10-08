using NUnit.Framework;
using UnityEngine;

namespace Kinema.MotionMatching.Tests
{
    /// <summary>
    /// What an inertialized transition does to one bone, frame by frame, without a rig: an outgoing
    /// motion is playing, the graph hard-switches to an incoming one, and the output must neither
    /// jump nor change speed at the seam, then settle exactly onto the incoming motion.
    ///
    /// The failure these pin: the offset's initial velocity used to be the outgoing velocity alone,
    /// so at the seam the output moved at outgoing + incoming - twice as fast for a well-matched
    /// jump - and the switch frame repeated the previous pose (a one-frame freeze, then a lurch).
    /// </summary>
    public class InertializationMathTests
    {
        private const float Dt = 1f / 60f;
        private const float Duration = 0.15f;

        /// <summary>
        /// Runs <paramref name="frames"/> evaluations of one bone. Before <paramref name="switchFrame"/>
        /// the input is <paramref name="outgoing"/>, from it on <paramref name="incoming"/>; the
        /// transition is requested on the switch frame, as the controller does.
        /// </summary>
        private static Vector3[] Simulate(System.Func<float, Vector3> outgoing, System.Func<float, Vector3> incoming,
                                          int frames, int switchFrame, float[] deltaTimes = null)
        {
            var clock = InertializationMath.Clock.Create();
            var bone = new InertializationMath.Bone();
            var output = new Vector3[frames];
            float time = 0f;

            for (int f = 0; f < frames; f++)
            {
                float dt = deltaTimes != null ? deltaTimes[f] : Dt;
                time += dt;
                if (f == switchFrame) clock.Request(Duration);

                Vector3 input = f < switchFrame ? outgoing(time) : incoming(time);
                InertializationMath.Frame frame = InertializationMath.BeginFrame(ref clock, dt);
                InertializationMath.Process(frame, ref bone, input, Quaternion.identity, out Vector3 outPos, out _);
                InertializationMath.EndFrame(ref clock, frame);
                output[f] = outPos;
            }
            return output;
        }

        [Test]
        public void MatchedVelocity_CrossesTheSeamAtTheSameSpeed()
        {
            // Same 2 m/s on both sides, 4 cm apart: a pose that matches in velocity and nearly in
            // position, which is what a good search hands the inertializer.
            Vector3[] output = Simulate(t => new Vector3(2f * t, 0f, 0f), t => new Vector3(2f * t - 0.04f, 0f, 0f), 40, 10);

            // Through the seam itself (switch, capture, first decay frame) the speed is unchanged.
            // After that it may ease by what absorbing 4 cm in 0.15 s takes - at most 1.5 * 0.04 / 0.15
            // = 0.4 m/s - but never freeze (0) or double (4 m/s), which is what the seam used to do.
            float expectedStep = 2f * Dt;
            for (int f = 9; f < 40; f++)
            {
                float step = output[f].x - output[f - 1].x;
                float tolerance = f <= 12 ? 0.1f : 0.25f;
                Assert.AreEqual(expectedStep, step, expectedStep * tolerance,
                    $"frame {f}: the output must keep moving at 2 m/s through the switch, not freeze or double");
            }
        }

        [Test]
        public void Output_SettlesOntoTheIncomingMotion()
        {
            Vector3[] output = Simulate(t => new Vector3(2f * t, 0f, 0f), t => new Vector3(2f * t - 0.3f, 0f, 0f), 40, 10);

            float t39 = 40 * Dt;
            Assert.AreEqual(2f * t39 - 0.3f, output[39].x, 1e-4f, "after the blend the incoming clip plays untouched");
        }

        [Test]
        public void SwitchFrame_ContinuesTheOutgoingMotionInsteadOfFreezing()
        {
            Vector3[] output = Simulate(t => new Vector3(2f * t, 0f, 0f), t => new Vector3(5f, 0f, 0f), 20, 10);

            Assert.Greater(output[10].x, output[9].x + 1.5f * Dt,
                "the frame of the switch must not repeat the previous pose");
        }

        [Test]
        public void DifferentVelocities_StayContinuousInPositionAndVelocity()
        {
            // Outgoing runs forward at 3 m/s, incoming stands still: the hardest case for velocity.
            Vector3[] output = Simulate(t => new Vector3(3f * t, 0f, 0f), t => new Vector3(0.4f, 0f, 0f), 40, 10);

            float before = output[9].x - output[8].x;
            Assert.AreEqual(before, output[10].x - output[9].x, 1e-4f, "switch frame: same velocity as before");
            Assert.AreEqual(before, output[11].x - output[10].x, 1e-4f, "capture frame: same velocity as before");

            // From there the offset (x0, v0) decays along a cubic Hermite, whose acceleration is bounded
            // by 6|x0|/D^2 + 4|v0|/D. Anything above that bound per frame is a kick, not a decay.
            float x0 = output[11].x - 0.4f;
            float v0 = 3f;
            float bound = (6f * Mathf.Abs(x0) / (Duration * Duration) + 4f * v0 / Duration) * Dt * Dt;
            for (int f = 12; f < 40; f++)
            {
                float a = (output[f].x - output[f - 1].x) - (output[f - 1].x - output[f - 2].x);
                Assert.LessOrEqual(Mathf.Abs(a), bound * 1.05f, $"frame {f}: the decay must stay within the curve's own braking");
            }
        }

        [Test]
        public void ZeroDeltaTime_DoesNotBlowUpTheOffset()
        {
            // A paused game: the switch happens on a zero-length evaluation, then time resumes.
            var dts = new float[30];
            for (int i = 0; i < dts.Length; i++) dts[i] = Dt;
            dts[10] = 0f;
            dts[11] = 0f;
            Vector3[] output = Simulate(t => new Vector3(2f * t, 0f, 0f), t => new Vector3(2f * t - 0.3f, 0f, 0f), 30, 10, dts);

            for (int f = 1; f < output.Length; f++)
                Assert.Less(Mathf.Abs(output[f].x - output[f - 1].x), 0.1f, $"frame {f}: no teleport");
        }

        [Test]
        public void NoHistory_HardCuts()
        {
            Vector3[] output = Simulate(t => new Vector3(2f * t, 0f, 0f), t => new Vector3(5f, 0f, 0f), 5, 1);
            Assert.AreEqual(5f, output[1].x, 1e-5f, "one frame of history carries no velocity: cut, do not guess");
        }

        [Test]
        public void Rotation_CrossesTheSeamAtTheSameAngularSpeed()
        {
            var clock = InertializationMath.Clock.Create();
            var bone = new InertializationMath.Bone();
            const float rate = 180f; // deg/s about Y on both sides, 4 degrees apart.
            float previousAngle = 0f, previousStep = 0f;

            for (int f = 0; f < 20; f++)
            {
                float time = (f + 1) * Dt;
                if (f == 10) clock.Request(Duration);
                float angle = f < 10 ? rate * time : rate * time - 4f;

                InertializationMath.Frame frame = InertializationMath.BeginFrame(ref clock, Dt);
                Quaternion inRot = InertializationMath.FromAxisAngle(new Vector3(0f, angle * Mathf.Deg2Rad, 0f));
                InertializationMath.Process(frame, ref bone, Vector3.zero, inRot, out _, out Quaternion outRot);
                InertializationMath.EndFrame(ref clock, frame);

                float outAngle = InertializationMath.ToAxisAngle(outRot).y * Mathf.Rad2Deg;
                float step = outAngle - previousAngle;
                if (f >= 2)
                    Assert.AreEqual(rate * Dt, step, rate * Dt * (f <= 12 ? 0.1f : 0.25f), $"frame {f}: angular speed through the seam");
                previousAngle = outAngle;
                previousStep = step;
            }
            Assert.Greater(previousStep, 0f);
        }

        [Test]
        public void AxisAngle_RoundTrips()
        {
            Quaternion q = InertializationMath.FromAxisAngle(new Vector3(0.3f, 0.9f, -0.2f).normalized * (73f * Mathf.Deg2Rad));
            Quaternion back = InertializationMath.FromAxisAngle(InertializationMath.ToAxisAngle(q));
            Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(q, back)), 1e-5f);
        }
    }
}
