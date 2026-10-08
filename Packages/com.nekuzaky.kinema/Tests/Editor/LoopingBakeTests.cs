using NUnit.Framework;
using UnityEngine;
using Kinema.MotionMatching.Editor;

namespace Kinema.MotionMatching.Tests
{
    /// <summary>
    /// Feature assembly on a looping clip, from hand-made samples (no rig, no AnimationClip): a
    /// looping clip plays as an endless cycle, so its trajectory must run on past its last frame
    /// instead of stopping there.
    ///
    /// The failure these pin: future and past trajectory points were clamped to the clip's own
    /// frames, so every frame in the last second of a run loop was baked as a character coming to a
    /// stop - and a running character was sent back to the start of its loop, again and again,
    /// never completing a stride.
    /// </summary>
    public class LoopingBakeTests
    {
        private const int Fps = 30;
        private const float Speed = 3f;

        private static FeatureSchema Schema() => new FeatureSchema
        {
            TrajectoryTimes = new[] { -0.4f, 0.5f, 1.0f },
            BoneNames = new[] { "Foot" },
            BoneWeights = new[] { 1f }
        };

        /// <summary>A 0.7 s cycle travelling straight along +Z at <see cref="Speed"/>, foot swinging along X.</summary>
        private static PoseExtractor.ClipSamples StraightRun(bool looping, float turnDegreesPerCycle = 0f)
        {
            const int frames = 21; // 0.7 s at 30 fps
            float dt = 1f / Fps;
            var rootPos = new Vector3[frames];
            var rootFwd = new Vector3[frames];
            var bonePos = new Vector3[frames];
            for (int f = 0; f < frames; f++)
            {
                float t = f * dt;
                float yaw = turnDegreesPerCycle * (t / 0.7f) * Mathf.Deg2Rad;
                rootFwd[f] = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
                rootPos[f] = turnDegreesPerCycle == 0f ? new Vector3(0f, 0f, Speed * t) : rootPos[Mathf.Max(f - 1, 0)] + rootFwd[f] * (f > 0 ? Speed * dt : 0f);
                bonePos[f] = rootPos[f] + new Vector3(0.2f * Mathf.Sin(t / 0.7f * 2f * Mathf.PI), 0.05f, 0f);
            }
            return new PoseExtractor.ClipSamples(rootPos, rootFwd, bonePos, 1, dt, 0.7f, looping);
        }

        private static float[] Assemble(PoseExtractor.ClipSamples samples, FeatureSchema schema)
        {
            return PoseExtractor.AssembleFeatures(samples, schema, Fps, new[] { 0 }, out _);
        }

        [Test]
        public void LoopingClip_FutureTrajectoryRunsOnPastTheLastFrame()
        {
            FeatureSchema schema = Schema();
            float[] features = Assemble(StraightRun(looping: true), schema);
            int dim = schema.Dimension;

            // Every frame, including the last ones, sees the run carry on: +1.0 s is 3 m ahead.
            for (int f = 0; f < 21; f++)
            {
                float futureZ = features[f * dim + schema.TrajectoryPositionOffset + 2 * 2 + 1];
                Assert.AreEqual(Speed * 1.0f, futureZ, 0.11f, $"frame {f}: the +1.0 s point of a run loop is 3 m ahead");
                float pastZ = features[f * dim + schema.TrajectoryPositionOffset + 1];
                Assert.AreEqual(-Speed * 0.4f, pastZ, 0.11f, $"frame {f}: the -0.4 s point of a run loop is 1.2 m behind");
            }
        }

        [Test]
        public void OneShotClip_StillClampsToItsOwnFrames()
        {
            FeatureSchema schema = Schema();
            float[] features = Assemble(StraightRun(looping: false), schema);
            int dim = schema.Dimension;

            // The last frame of a one-shot really does end there: nothing ahead of it.
            float futureZ = features[20 * dim + schema.TrajectoryPositionOffset + 2 * 2 + 1];
            Assert.AreEqual(0f, futureZ, 1e-4f);
        }

        [Test]
        public void LoopingClip_VelocityAtTheSeamMatchesTheMiddle()
        {
            FeatureSchema schema = Schema();
            float[] features = Assemble(StraightRun(looping: true), schema);
            int dim = schema.Dimension;

            float middle = features[10 * dim + schema.RootVelocityOffset + 1];
            Assert.AreEqual(Speed, middle, 1e-3f);
            Assert.AreEqual(middle, features[0 * dim + schema.RootVelocityOffset + 1], 1e-3f, "first frame, measured across the seam");
            Assert.AreEqual(middle, features[20 * dim + schema.RootVelocityOffset + 1], 1e-3f, "last frame, measured across the seam");
        }

        [Test]
        public void TurningLoop_NextCycleContinuesTheTurn()
        {
            // 90 degrees per 0.7 s cycle: one cycle on, the root faces 90 degrees further round.
            PoseExtractor.ClipSamples samples = StraightRun(looping: true, turnDegreesPerCycle: 90f);

            Vector3 start = samples.Forward(0);
            Vector3 nextCycle = samples.Forward(21);
            float turned = Vector3.SignedAngle(start, nextCycle, Vector3.up);
            Assert.AreEqual(90f, turned, 2f);

            // And the path is continuous through the seam: the step across it is a normal step.
            float seamStep = (samples.Root(21) - samples.Root(20)).magnitude;
            float normalStep = (samples.Root(11) - samples.Root(10)).magnitude;
            Assert.AreEqual(normalStep, seamStep, normalStep * 0.05f);
        }
    }
}
