using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Kinema.MotionMatching.Tests
{
    /// <summary>
    /// A ticking character allocates nothing on the managed heap: searches, jumps, transitions and
    /// the graph evaluation included. Garbage collection is a stall of its own - a hitch that has
    /// nothing to do with the animation - and a per-frame allocation on every character is how a
    /// crowd scene buys one every few seconds.
    ///
    /// The warm-up absorbs everything that is legitimately allocated once: the first search's
    /// buffers, the matcher's lazily built state.
    /// </summary>
    public sealed class AllocationPlayModeTests
    {
        private PlayModeTestRig _rig;

        [SetUp]
        public void SetUp()
        {
            _rig = PlayModeTestRig.Create();
            _rig.Controller.DesiredVelocity = new Vector3(0f, 0f, 1.2f);
            _rig.Step(120);
        }

        [TearDown]
        public void TearDown() => _rig.Dispose();

        [Test]
        public void SteadyTicking_AllocatesNothing()
        {
            Assert.That(() => _rig.Step(60), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void SearchesAndTransitions_AllocateNothing()
        {
            // Intent changes force deviation searches and jumps - the paths that used to allocate.
            Assert.That(() =>
            {
                _rig.Controller.DesiredVelocity = Vector3.right;
                _rig.Step(15);
                _rig.Controller.DesiredVelocity = Vector3.zero;
                _rig.Step(15);
                _rig.Controller.DesiredVelocity = new Vector3(-1f, 0f, -1f);
                _rig.Step(15);
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
