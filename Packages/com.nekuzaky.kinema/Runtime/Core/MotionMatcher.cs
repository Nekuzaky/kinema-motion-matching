using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Result of a single database search: the winning frame plus a full per-group cost
    /// breakdown so the debug layer can explain *why* that frame won.
    /// </summary>
    public struct MotionMatchResult
    {
        public int FrameIndex;
        public float TotalCost;

        /// <summary>
        /// Cost contribution of each <see cref="FeatureGroup"/> for the winning frame. Owned by the
        /// matcher that produced the result and rewritten by its next search - copy it to keep it.
        /// (A fresh array per search was the matcher's only steady allocation.)
        /// </summary>
        public float[] GroupCosts;

        public float TrajectoryCost => GroupCosts[(int)FeatureGroup.TrajectoryPosition]
                                     + GroupCosts[(int)FeatureGroup.TrajectoryDirection];

        public float PoseCost => GroupCosts[(int)FeatureGroup.BonePosition]
                               + GroupCosts[(int)FeatureGroup.BoneVelocity]
                               + GroupCosts[(int)FeatureGroup.RootVelocity];

        public bool IsValid => FrameIndex >= 0;
    }

    /// <summary>How the nearest-neighbour search is executed.</summary>
    public enum SearchAcceleration
    {
        /// <summary>Burst-compiled parallel linear scan. Best default up to ~50k frames; supports tag filtering.</summary>
        BurstLinear,
        /// <summary>KD-tree over weight-scaled features. For very large databases; falls back to the
        /// linear job whenever tag masks or ignore ranges are active.</summary>
        KdTree
    }

    /// <summary>
    /// Weighted nearest-neighbour search over the database, executed as a Burst-compiled parallel
    /// job: the frame range is split into chunks, each worker scans its chunk with a weighted squared
    /// distance and a branch-and-bound early-out, and the per-chunk winners are reduced on the main
    /// thread. Features are uploaded once into a persistent <see cref="NativeArray{T}"/>.
    /// An optional KD-tree path serves very large databases. Owns native memory: call <see cref="Dispose"/>.
    /// </summary>
    public sealed class MotionMatcher : IDisposable
    {
        #region Private and Protected

        private const int ChunkSize = 256;

        private readonly MotionMatchingDatabase _database;
        private float[] _perDimensionWeights;
        private KdTreeSearch _kdTree;

        private NativeArray<float> _nativeFeatures;
        private NativeArray<float> _nativeWeights;
        private NativeArray<float> _nativeQuery;
        private NativeArray<ulong> _nativeTags;
        private NativeArray<float> _nativePhases;
        // 1 = the frame may be selected. Everything is searchable until SetClipEndExclusion says otherwise.
        private NativeArray<byte> _nativeSearchable;
        private bool _hasExclusions;
        private float _phaseWeight;
        private readonly float[] _groupCosts = new float[FeatureGroupExtensions.Count];
        private NativeArray<float> _chunkBestCost;
        private NativeArray<int> _chunkBestFrame;
        private readonly int _chunkCount;
        private bool _disposed;

        #endregion

        #region Public

        public MotionMatcher(MotionMatchingDatabase database, FeatureWeights weights)
        {
            _database = database;

            _nativeFeatures = new NativeArray<float>(database.Features, Allocator.Persistent);
            _nativeWeights = new NativeArray<float>(database.Dimension, Allocator.Persistent);
            _nativeQuery = new NativeArray<float>(database.Dimension, Allocator.Persistent);
            _nativeTags = database.HasTags
                ? new NativeArray<ulong>(database.FrameTags, Allocator.Persistent)
                : new NativeArray<ulong>(database.FrameCount, Allocator.Persistent); // zeroed = untagged
            _chunkCount = (database.FrameCount + ChunkSize - 1) / ChunkSize;
            _nativePhases = database.HasFootPhases
                ? new NativeArray<float>(database.FootPhases, Allocator.Persistent)
                : new NativeArray<float>(database.FrameCount, Allocator.Persistent); // zeroed -> treated as no phase via weight gate
            if (!database.HasFootPhases)
                for (int i = 0; i < _nativePhases.Length; i++) _nativePhases[i] = -1f;
            _chunkBestCost = new NativeArray<float>(_chunkCount, Allocator.Persistent);
            _chunkBestFrame = new NativeArray<int>(_chunkCount, Allocator.Persistent);
            _nativeSearchable = new NativeArray<byte>(database.FrameCount, Allocator.Persistent);
            for (int i = 0; i < _nativeSearchable.Length; i++) _nativeSearchable[i] = 1;

            UpdateWeights(weights);
        }

        public MotionMatchingDatabase Database => _database;

        /// <summary>Search strategy. KD-tree is rebuilt lazily after weight changes.</summary>
        public SearchAcceleration Acceleration { get; set; } = SearchAcceleration.BurstLinear;

        /// <summary>Seconds at the end of every non-looping clip that the search never selects.</summary>
        public float ClipEndExclusion { get; private set; }

        /// <summary>
        /// Stops the search from selecting the last <paramref name="seconds"/> of every non-looping
        /// clip (capped at half the clip, so a short clip stays reachable). A frame picked there has
        /// nowhere to go: the clip clamps at its end and the character holds a frozen pose, with no
        /// root motion, until some later search finds a way out - and the frame that best matches
        /// a character standing on the last frame of a clip is usually the last frame of that clip.
        /// Holden's reference implementation skips the end of every range for the same reason.
        /// Playback still reaches those frames by continuing; only jumping into them is ruled out.
        /// 0 = every frame is a candidate (the default, and what the tests' synthetic databases need).
        /// </summary>
        public void SetClipEndExclusion(float seconds)
        {
            ClipEndExclusion = Mathf.Max(0f, seconds);
            _hasExclusions = false;
            for (int f = 0; f < _database.FrameCount; f++)
            {
                byte searchable = 1;
                if (ClipEndExclusion > 0f)
                {
                    MotionFrameInfo frame = _database.GetFrame(f);
                    if (frame.ClipIndex >= 0 && frame.ClipIndex < _database.ClipCount)
                    {
                        MotionClipEntry clip = _database.GetClip(frame.ClipIndex);
                        float window = Mathf.Min(ClipEndExclusion, clip.Length * 0.5f);
                        if (!clip.IsLooping && clip.Length > 0f && frame.Time > clip.Length - window)
                            searchable = 0;
                    }
                }
                _nativeSearchable[f] = searchable;
                _hasExclusions |= searchable == 0;
            }
        }

        #endregion

        #region Main API

        /// <summary>Rebuilds the cached per-dimension weight table. Call when weights change.</summary>
        public void UpdateWeights(FeatureWeights weights)
        {
            if (_perDimensionWeights == null || _perDimensionWeights.Length != _database.Dimension)
                _perDimensionWeights = new float[_database.Dimension];
            _database.Schema.BuildPerDimensionWeights(weights, _perDimensionWeights);
            _nativeWeights.CopyFrom(_perDimensionWeights);
            _phaseWeight = weights.FootPhase;
            _kdTree?.Invalidate(); // metric changed: the scaled tree is stale.
        }

        /// <summary>
        /// Finds the lowest-cost frame for the given query. <paramref name="ignoreFrame"/> and its
        /// close neighbours can be excluded to avoid re-selecting the frame already playing.
        /// <paramref name="requiredTags"/>: candidate frames must carry ALL these tag bits;
        /// <paramref name="excludedTags"/>: frames carrying ANY of these bits are skipped.
        /// </summary>
        public MotionMatchResult Search(
            MotionMatchingQuery query, int ignoreFrame = -1, int ignoreRadius = 0,
            ulong requiredTags = 0ul, ulong excludedTags = 0ul)
        {
            bool phaseActive = _phaseWeight > 0f && query.FootPhase >= 0f && _database.HasFootPhases;

            // KD-tree path: exact under the weighted metric, but cannot express tag filters, ignore
            // ranges, clip-end exclusions or the phase term - use it only when none of those are active.
            if (Acceleration == SearchAcceleration.KdTree
                && requiredTags == 0ul && excludedTags == 0ul && ignoreFrame < 0 && !phaseActive && !_hasExclusions)
            {
                _kdTree ??= new KdTreeSearch();
                if (!_kdTree.IsBuilt)
                    _kdTree.Build(_database.Features, _database.FrameCount, _database.Dimension, _perDimensionWeights);

                int frame = _kdTree.Nearest(query.Values, _perDimensionWeights, out float kdCost);
                return BuildResult(query, frame, kdCost);
            }

            _nativeQuery.CopyFrom(query.Values);

            var job = new SearchJob
            {
                Features = _nativeFeatures,
                Weights = _nativeWeights,
                Query = _nativeQuery,
                Tags = _nativeTags,
                Searchable = _nativeSearchable,
                RequiredTags = requiredTags,
                ExcludedTags = excludedTags,
                Phases = _nativePhases,
                QueryPhase = phaseActive ? query.FootPhase : -1f,
                PhaseWeight = phaseActive ? _phaseWeight : 0f,
                Dimension = _database.Dimension,
                FrameCount = _database.FrameCount,
                ChunkSize = ChunkSize,
                IgnoreStart = ignoreFrame >= 0 ? ignoreFrame - ignoreRadius : -1,
                IgnoreEnd = ignoreFrame >= 0 ? ignoreFrame + ignoreRadius : -1,
                BestCost = _chunkBestCost,
                BestFrame = _chunkBestFrame
            };

            job.Schedule(_chunkCount, 1).Complete();
            return CompleteChunks(query);
        }

        /// <summary>
        /// Non-blocking half of <see cref="Search"/>: schedules the same Burst job but does not wait
        /// for it, so a caller managing several matchers (one per on-screen character, say) can
        /// schedule all of them first and let Burst's worker threads run their chunks in parallel,
        /// then <see cref="CompleteSearch"/> each in turn - instead of every matcher blocking the main
        /// thread in series the way <see cref="Search"/> does. No controller uses this yet
        /// (per-character batching would be a new orchestrating component); it exists so that
        /// opportunity can be profiled and, later, built on. Must be paired with exactly one
        /// <see cref="CompleteSearch"/> call using the same <paramref name="query"/> before this
        /// matcher's <see cref="Search"/>/<see cref="ScheduleSearch"/> is called again - the chunk
        /// result buffers are reused, not per-call. Always uses the Burst job (ignores
        /// <see cref="Acceleration"/> == KdTree, which is synchronous managed code with nothing to
        /// schedule) - batching only makes sense for the parallel path.
        /// </summary>
        public JobHandle ScheduleSearch(
            MotionMatchingQuery query, int ignoreFrame = -1, int ignoreRadius = 0,
            ulong requiredTags = 0ul, ulong excludedTags = 0ul)
        {
            bool phaseActive = _phaseWeight > 0f && query.FootPhase >= 0f && _database.HasFootPhases;
            _nativeQuery.CopyFrom(query.Values);

            var job = new SearchJob
            {
                Features = _nativeFeatures,
                Weights = _nativeWeights,
                Query = _nativeQuery,
                Tags = _nativeTags,
                Searchable = _nativeSearchable,
                RequiredTags = requiredTags,
                ExcludedTags = excludedTags,
                Phases = _nativePhases,
                QueryPhase = phaseActive ? query.FootPhase : -1f,
                PhaseWeight = phaseActive ? _phaseWeight : 0f,
                Dimension = _database.Dimension,
                FrameCount = _database.FrameCount,
                ChunkSize = ChunkSize,
                IgnoreStart = ignoreFrame >= 0 ? ignoreFrame - ignoreRadius : -1,
                IgnoreEnd = ignoreFrame >= 0 ? ignoreFrame + ignoreRadius : -1,
                BestCost = _chunkBestCost,
                BestFrame = _chunkBestFrame
            };

            _lastScheduled = job.Schedule(_chunkCount, 1);
            return _lastScheduled;
        }

        // Last handle ScheduleSearch produced, completed defensively by Dispose: disposing the
        // NativeArrays under a still-running job is a safety-system error. Complete on an already-
        // finished (or default) handle is a free no-op, so no bookkeeping flag is needed.
        private JobHandle _lastScheduled;

        /// <summary>Completes a <see cref="ScheduleSearch"/> handle and reduces its chunk results,
        /// exactly like the tail of <see cref="Search"/>. <paramref name="query"/> must be the same
        /// query instance passed to the matching <see cref="ScheduleSearch"/> call (needed to build
        /// the per-group cost breakdown in the result).</summary>
        public MotionMatchResult CompleteSearch(JobHandle handle, MotionMatchingQuery query)
        {
            handle.Complete();
            return CompleteChunks(query);
        }

        private MotionMatchResult CompleteChunks(MotionMatchingQuery query)
        {
            int bestFrame = -1;
            float bestCost = float.MaxValue;
            for (int c = 0; c < _chunkCount; c++)
            {
                if (_chunkBestFrame[c] >= 0 && _chunkBestCost[c] < bestCost)
                {
                    bestCost = _chunkBestCost[c];
                    bestFrame = _chunkBestFrame[c];
                }
            }

            return BuildResult(query, bestFrame, bestCost);
        }

        /// <summary>Computes the total cost of a single candidate frame (used by the debug tools).</summary>
        public float EvaluateCost(MotionMatchingQuery query, int frameIndex)
        {
            float[] features = _database.Features;
            float[] weights = _perDimensionWeights;
            float[] values = query.Values;
            int dimension = _database.Dimension;
            int offset = frameIndex * dimension;

            float cost = PhaseCost(query, frameIndex);
            for (int i = 0; i < dimension; i++)
            {
                float d = values[i] - features[offset + i];
                cost += weights[i] * d * d;
            }
            return cost;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lastScheduled.Complete();
            _nativeFeatures.Dispose();
            _nativeWeights.Dispose();
            _nativeQuery.Dispose();
            _nativeTags.Dispose();
            _nativePhases.Dispose();
            _nativeSearchable.Dispose();
            _chunkBestCost.Dispose();
            _chunkBestFrame.Dispose();
        }

        #endregion

        #region Tools and Utilities

        /// <summary>Circular phase distance term, shared by the managed cost paths.</summary>
        private float PhaseCost(MotionMatchingQuery query, int frameIndex)
        {
            if (_phaseWeight <= 0f || query.FootPhase < 0f || !_database.HasFootPhases) return 0f;
            float p = _database.GetFootPhase(frameIndex);
            if (p < 0f) return 0f;
            float d = Mathf.Abs(p - query.FootPhase);
            d = Mathf.Min(d, 1f - d) * 2f;
            return _phaseWeight * d * d;
        }

        [BurstCompile]
        private struct SearchJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Features;
            [ReadOnly] public NativeArray<float> Weights;
            [ReadOnly] public NativeArray<float> Query;
            [ReadOnly] public NativeArray<ulong> Tags;
            [ReadOnly] public NativeArray<byte> Searchable;
            [ReadOnly] public NativeArray<float> Phases;
            public float QueryPhase, PhaseWeight;
            public ulong RequiredTags, ExcludedTags;
            public int Dimension, FrameCount, ChunkSize;
            public int IgnoreStart, IgnoreEnd;

            [NativeDisableParallelForRestriction] public NativeArray<float> BestCost;
            [NativeDisableParallelForRestriction] public NativeArray<int> BestFrame;

            public void Execute(int chunk)
            {
                int start = chunk * ChunkSize;
                int end = Mathf.Min(start + ChunkSize, FrameCount);

                float best = float.MaxValue;
                int bestF = -1;

                for (int f = start; f < end; f++)
                {
                    if (f >= IgnoreStart && f <= IgnoreEnd) continue;
                    if (Searchable[f] == 0) continue;

                    ulong tags = Tags[f];
                    if ((tags & RequiredTags) != RequiredTags) continue;
                    if ((tags & ExcludedTags) != 0ul) continue;

                    int offset = f * Dimension;

                    // Foot-phase term first: a fixed candidate cost, so the branch-and-bound
                    // early-out below still sees a monotonically growing total.
                    float cost = 0f;
                    if (PhaseWeight > 0f && QueryPhase >= 0f)
                    {
                        float p = Phases[f];
                        if (p >= 0f)
                        {
                            float pd = Mathf.Abs(p - QueryPhase);
                            pd = Mathf.Min(pd, 1f - pd) * 2f;   // circular, normalized 0..1
                            cost = PhaseWeight * pd * pd;
                            if (cost >= best) continue;
                        }
                    }

                    for (int i = 0; i < Dimension; i++)
                    {
                        float d = Query[i] - Features[offset + i];
                        cost += Weights[i] * d * d;
                        if (cost >= best) break;
                    }
                    if (cost < best)
                    {
                        best = cost;
                        bestF = f;
                    }
                }

                BestCost[chunk] = best;
                BestFrame[chunk] = bestF;
            }
        }

        private MotionMatchResult BuildResult(MotionMatchingQuery query, int frameIndex, float totalCost)
        {
            float[] groupCosts = _groupCosts;
            System.Array.Clear(groupCosts, 0, groupCosts.Length);
            if (frameIndex < 0)
                return new MotionMatchResult { FrameIndex = -1, TotalCost = 0f, GroupCosts = groupCosts };

            FeatureSchema schema = _database.Schema;
            float[] features = _database.Features;
            float[] weights = _perDimensionWeights;
            float[] values = query.Values;
            int dimension = _database.Dimension;
            int offset = frameIndex * dimension;

            for (int i = 0; i < dimension; i++)
            {
                float d = values[i] - features[offset + i];
                groupCosts[(int)schema.GetGroupOf(i)] += weights[i] * d * d;
            }

            return new MotionMatchResult
            {
                FrameIndex = frameIndex,
                TotalCost = totalCost,
                GroupCosts = groupCosts
            };
        }

        #endregion
    }
}
