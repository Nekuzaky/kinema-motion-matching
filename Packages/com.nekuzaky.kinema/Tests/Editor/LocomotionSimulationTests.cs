using NUnit.Framework;
using UnityEngine;

namespace Kinema.MotionMatching.Tests
{
    /// <summary>
    /// The simulated character root motion is synced toward: it must follow exactly the path the
    /// trajectory prediction promised the search, at any frame rate, and the sync must never drag a
    /// standing animation or slide a moving one by more than its ratio.
    /// </summary>
    public class LocomotionSimulationTests
    {
        [Test]
        public void Step_IsIndependentOfFrameRate()
        {
            var desired = new Vector3(3f, 0f, 1f);
            Vector3 p60 = Vector3.zero, v60 = new Vector3(0f, 0f, -1f), a60 = Vector3.zero;
            Vector3 p30 = p60, v30 = v60, a30 = a60;

            for (int i = 0; i < 60; i++) LocomotionSimulation.Step(ref p60, ref v60, ref a60, desired, 0.35f, 1f / 60f);
            for (int i = 0; i < 30; i++) LocomotionSimulation.Step(ref p30, ref v30, ref a30, desired, 0.35f, 1f / 30f);

            Assert.AreEqual(p30.x, p60.x, 1e-4f);
            Assert.AreEqual(p30.z, p60.z, 1e-4f);
            Assert.AreEqual(v30.x, v60.x, 1e-4f);
        }

        [Test]
        public void Step_FollowsThePredictedTrajectory()
        {
            // From rest at 1 m/s forward to 3 m/s sideways: the simulation after t seconds must sit
            // where TrajectoryPredictor's critical spring put its point at time t.
            var current = new Vector3(0f, 0f, 1f);
            var desired = new Vector3(3f, 0f, 0f);
            const float response = 0.35f;
            const float t = 0.6f;

            Vector3 p = Vector3.zero, v = current, a = Vector3.zero;
            for (int i = 0; i < 36; i++) LocomotionSimulation.Step(ref p, ref v, ref a, desired, response, t / 36f);

            float w = 2f / response;
            float blend = (2f - Mathf.Exp(-w * t) * (2f + w * t)) / w;
            Vector3 predicted = desired * t + (current - desired) * blend;

            Assert.AreEqual(predicted.x, p.x, 1e-4f);
            Assert.AreEqual(predicted.z, p.z, 1e-4f);
        }

        [Test]
        public void Step_SettlesOnTheDesiredVelocityWithoutOvershoot()
        {
            Vector3 p = Vector3.zero, v = Vector3.zero, a = Vector3.zero;
            var desired = new Vector3(0f, 0f, 2f);
            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                LocomotionSimulation.Step(ref p, ref v, ref a, desired, 0.35f, 1f / 60f);
                peak = Mathf.Max(peak, v.z);
            }
            Assert.AreEqual(2f, v.z, 1e-3f);
            Assert.LessOrEqual(peak, 2f + 1e-4f, "critically damped: never faster than asked");
        }

        [Test]
        public void Adjust_NeverDragsAStandingAnimation()
        {
            Vector3 delta = LocomotionSimulation.Adjust(Vector3.zero, new Vector3(1f, 0f, 0f), 0.15f, 0.5f, 1f / 60f);
            Assert.AreEqual(0f, delta.magnitude, 1e-6f, "no animation movement, no nudge: the feet cannot slide on the spot");
        }

        [Test]
        public void Adjust_IsBoundedByTheRatioOfTheAnimationsOwnMovement()
        {
            var animation = new Vector3(0f, 0f, 0.05f); // 3 m/s at 60 fps
            Vector3 delta = LocomotionSimulation.Adjust(animation, new Vector3(10f, 0f, 0f), 0.15f, 0.4f, 1f / 60f);
            Vector3 nudge = delta - animation;
            Assert.AreEqual(0.4f * 0.05f, nudge.magnitude, 1e-5f);
            Assert.Greater(nudge.x, 0f, "towards the simulation");
        }

        [Test]
        public void Adjust_ClosesASmallGapWithoutOvershooting()
        {
            var animation = new Vector3(0f, 0f, 0.05f);
            var gap = new Vector3(0.001f, 0f, 0f);
            Vector3 nudge = LocomotionSimulation.Adjust(animation, gap, 0.15f, 1f, 1f / 60f) - animation;
            Assert.Greater(nudge.x, 0f);
            Assert.Less(nudge.x, gap.x, "an exponential decay of the gap, never past it");
            Assert.AreEqual(0f, nudge.y, 1e-6f, "horizontal only - gravity and steps stay the motor's");
        }

        [Test]
        public void Leash_HoldsTheSimulationNearAStoppedCharacter()
        {
            Vector3 leashed = LocomotionSimulation.Leash(new Vector3(5f, 2f, 0f), new Vector3(1f, 0f, 0f), 0.5f);
            Assert.AreEqual(1.5f, leashed.x, 1e-5f);
            Assert.AreEqual(0f, leashed.y, 1e-5f, "the simulation stays at the character's height");
        }

        [Test]
        public void Mirror_ReflectsAcrossTheCharactersOwnSide()
        {
            // Facing +X (yaw 90), stepping forward and to the character's left: world (+0.1, 0, +0.02).
            Quaternion facing = new Quaternion(0f, Mathf.Sin(Mathf.PI / 4f), 0f, Mathf.Cos(Mathf.PI / 4f));
            var step = new Vector3(0.1f, 0f, 0.02f);
            // A 10 degree turn to the left (negative yaw).
            float half = -10f * Mathf.Deg2Rad * 0.5f;
            var turn = new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));

            LocomotionSimulation.Mirror(facing, ref step, ref turn);

            Assert.AreEqual(0.1f, step.x, 1e-5f, "still forward");
            Assert.AreEqual(-0.02f, step.z, 1e-5f, "now to the character's right");
            Assert.AreEqual(-Mathf.Sin(half), turn.y, 1e-6f, "and turning right by the same angle");
        }
    }
}
