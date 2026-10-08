using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>Receives searches scheduled (not yet completed) by controllers whose
    /// <see cref="MotionMatchingController.SearchScheduler"/> points at this object.</summary>
    public interface IMotionSearchScheduler
    {
        void EnqueueScheduledSearch(MotionMatchingController controller, JobHandle handle);
    }

    /// <summary>
    /// Turns the benchmark's measured batching win (~1.7x at 8-128 characters searching the same
    /// frame) into something usable: assign the controllers (or let it collect every controller in
    /// the scene on enable), and each one that decides to search during Update schedules its Burst
    /// job instead of blocking on it; this component completes them all in LateUpdate, after every
    /// controller has had its chance to schedule - so the jobs genuinely overlap on worker threads.
    /// Cost of admission: a jump decided by a batched search takes effect one graph evaluation later
    /// than the synchronous path (documented on the controller hook). With controllers searching on
    /// staggered timers, frames where only one search fires see no benefit - the win appears when
    /// several land on the same frame, which is exactly the case that used to spike.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Motion Matching Search Batch")]
    [DefaultExecutionOrder(1000)] // complete after every controller's Update has scheduled.
    public sealed class MotionMatchingSearchBatch : MonoBehaviour, IMotionSearchScheduler
    {
        [Tooltip("Controllers to batch. Leave empty to collect every controller in the scene when this component enables.")]
        [SerializeField] private MotionMatchingController[] _controllers = System.Array.Empty<MotionMatchingController>();

        private readonly List<(MotionMatchingController controller, JobHandle handle)> _pending
            = new List<(MotionMatchingController, JobHandle)>(32);

        /// <summary>Controllers currently routed through this batch (read-only view for tests/tools).</summary>
        public IReadOnlyList<MotionMatchingController> Controllers => _controllers;

        private void OnEnable()
        {
            if (_controllers == null || _controllers.Length == 0)
                _controllers = UnityCompat.FindAll<MotionMatchingController>();
            foreach (MotionMatchingController controller in _controllers)
                if (controller != null) controller.SearchScheduler = this;
        }

        private void OnDisable()
        {
            // Never leave a scheduled job dangling - and give controllers their synchronous path back.
            CompletePendingSearches();
            foreach (MotionMatchingController controller in _controllers)
                if (controller != null && ReferenceEquals(controller.SearchScheduler, this))
                    controller.SearchScheduler = null;
        }

        public void EnqueueScheduledSearch(MotionMatchingController controller, JobHandle handle)
        {
            _pending.Add((controller, handle));
        }

        /// <summary>Routes a controller spawned after this batch enabled (the OnEnable auto-collect
        /// only sees what already exists) through this batch.</summary>
        public void Register(MotionMatchingController controller)
        {
            if (controller == null) return;
            controller.SearchScheduler = this;
            for (int i = 0; i < _controllers.Length; i++)
                if (_controllers[i] == controller) return;
            var grown = new MotionMatchingController[_controllers.Length + 1];
            _controllers.CopyTo(grown, 0);
            grown[_controllers.Length] = controller;
            _controllers = grown;
        }

        /// <summary>Hands a controller its synchronous search path back.</summary>
        public void Unregister(MotionMatchingController controller)
        {
            if (controller != null && ReferenceEquals(controller.SearchScheduler, this))
                controller.SearchScheduler = null;
        }

        private static readonly Unity.Profiling.ProfilerMarker CompleteMarker =
            new Unity.Profiling.ProfilerMarker("Kinema.CompleteBatchedSearches");

        private void LateUpdate()
        {
            using (CompleteMarker.Auto()) CompletePendingSearches();
        }

        /// <summary>
        /// Completes every search scheduled since the last call and applies its outcome. Called
        /// automatically in LateUpdate; call it yourself after stepping controllers set to
        /// <see cref="MotionMatchingController.TickMode.Manual"/>, so their jobs are still batched
        /// (schedule them all, then complete them all) under your own tick.
        /// </summary>
        public void CompletePendingSearches()
        {
            for (int i = 0; i < _pending.Count; i++)
                _pending[i].controller.CompleteScheduledSearch(_pending[i].handle);
            _pending.Clear();
        }
    }
}
