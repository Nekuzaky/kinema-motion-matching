using System.Collections.Generic;
using UnityEngine;

namespace Kinema.MotionMatching.Editor
{
    /// <summary>
    /// Samples a single <see cref="AnimationClip"/> on a rig and turns it into a flat array of
    /// raw (un-normalized) feature rows following a <see cref="FeatureSchema"/>. Pure sampling and
    /// math: normalization and database assembly are the baker's job.
    /// </summary>
    public static class PoseExtractor
    {
        #region Main API

        /// <summary>
        /// Resolves the schema's bone names against a rig hierarchy. Missing bones are reported so the
        /// baker can surface them; their slot is returned as null and later sampled as the root.
        /// </summary>
        public static Transform[] ResolveBones(GameObject rig, FeatureSchema schema, out List<string> missing)
        {
            missing = new List<string>();
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in rig.GetComponentsInChildren<Transform>(true))
                byName[t.name] = t; // last-wins is fine; rigs rarely duplicate meaningful bone names.

            var bones = new Transform[schema.BoneCount];
            for (int b = 0; b < schema.BoneCount; b++)
            {
                if (byName.TryGetValue(schema.BoneNames[b], out Transform bone))
                    bones[b] = bone;
                else
                    missing.Add(schema.BoneNames[b]);
            }
            return bones;
        }

        /// <summary>Foot-contact detection thresholds (character space, rig at origin).</summary>
        public const float ContactMaxSpeed = 0.30f;   // m/s
        public const float ContactMaxHeight = 0.15f;  // m above ground

        /// <summary>
        /// Samples <paramref name="clip"/> at <paramref name="fps"/> and writes one feature row per
        /// frame into a freshly allocated array of length frameCount * schema.Dimension.
        /// <paramref name="contactBones"/> lists schema-bone indices treated as feet;
        /// <paramref name="contacts"/> receives one byte per frame (bit b = contact bone b grounded).
        ///
        /// A looping clip is sampled as the endless cycle it plays as: trajectory points past its end
        /// come from the next cycle (the root carried on by one cycle's travel and turn), points
        /// before its start from the previous one, and velocities at the seam are measured across it.
        /// Clamping those to the clip's own range - as for a one-shot - told the database that every
        /// frame near the end of a run loop was coming to a stop, so a running character kept being
        /// sent back to the start of its loop and never completed a stride.
        /// </summary>
        public static float[] ExtractRawFeatures(
            GameObject rig, Transform root, Transform[] bones,
            AnimationClip clip, FeatureSchema schema, int fps,
            int[] contactBones, out byte[] contacts, out int frameCount)
        {
            frameCount = Mathf.Max(1, Mathf.CeilToInt(clip.length * fps));
            int boneCount = schema.BoneCount;
            float dt = 1f / fps;

            // --- Pass 1: sample world-space root + bone transforms for every frame. ---
            var rootPos = new Vector3[frameCount];
            var rootFwd = new Vector3[frameCount];
            var bonePos = new Vector3[frameCount * boneCount];

            for (int f = 0; f < frameCount; f++)
            {
                float time = Mathf.Min(f * dt, clip.length);
                clip.SampleAnimation(rig, time);

                rootPos[f] = root.position;
                rootFwd[f] = root.forward;
                for (int b = 0; b < boneCount; b++)
                    bonePos[f * boneCount + b] = bones[b] != null ? bones[b].position : root.position;
            }

            var samples = new ClipSamples(rootPos, rootFwd, bonePos, boneCount, dt, clip.length, clip.isLooping);
            return AssembleFeatures(samples, schema, fps, contactBones, out contacts);
        }

        /// <summary>
        /// Pass 2 of <see cref="ExtractRawFeatures"/>, on already-sampled transforms: assembles the
        /// feature rows in schema layout, in character space. Separate so it can be tested without a
        /// rig or a clip.
        /// </summary>
        public static float[] AssembleFeatures(ClipSamples samples, FeatureSchema schema, int fps,
                                               int[] contactBones, out byte[] contacts)
        {
            int frameCount = samples.FrameCount;
            int dim = schema.Dimension;
            var features = new float[frameCount * dim];
            float[] times = schema.TrajectoryTimes;

            for (int f = 0; f < frameCount; f++)
            {
                int rowOffset = f * dim;
                var space = new CharacterSpace(samples.Root(f), samples.Forward(f));

                WriteTrajectory(features, rowOffset, schema, space, samples, f, times, fps);
                WriteBones(features, rowOffset, schema, space, samples, f);
                WriteRootVelocity(features, rowOffset, schema, space, samples, f);
            }

            contacts = ExtractContacts(samples, contactBones);
            return features;
        }

        /// <summary>
        /// One clip's sampled root and bone transforms, readable at any frame index - including
        /// indices before its start and past its end. A one-shot clamps those to its first and last
        /// frame; a looping clip wraps them into the previous or next cycle, carried by the root's
        /// travel and turn over one cycle.
        /// </summary>
        public sealed class ClipSamples
        {
            private readonly Vector3[] _rootPos, _rootFwd, _bonePos;
            private readonly float _dt, _length;

            // One cycle's root displacement, as a rigid move: rotate about Y by _cycleTurn around
            // the cycle's start, then carry the start to the cycle's end.
            private readonly Vector3 _cycleStart, _cycleEnd;
            private readonly Quaternion _cycleTurn;

            public int FrameCount { get; }
            public int BoneCount { get; }
            public bool Looping { get; }

            public ClipSamples(Vector3[] rootPos, Vector3[] rootFwd, Vector3[] bonePos, int boneCount,
                               float dt, float length, bool looping)
            {
                _rootPos = rootPos;
                _rootFwd = rootFwd;
                _bonePos = bonePos;
                BoneCount = boneCount;
                FrameCount = rootPos.Length;
                _dt = dt;
                _length = length;
                // A single frame has no cycle to speak of.
                Looping = looping && FrameCount > 1 && length > 0f;
                _cycleTurn = Quaternion.identity;
                if (!Looping) return;

                // Where the root is one full cycle on, extrapolated from the last frame's own motion
                // over the time left to the loop point (the last sample sits up to one frame short of
                // it). Extrapolated rather than sampled at clip.length: whether sampling a looping clip
                // exactly at its length lands on the end of the cycle or wraps back to its start is not
                // something to stake the whole database on.
                int last = FrameCount - 1;
                float remaining = Mathf.Clamp(length - last * dt, 0f, dt) / dt;
                _cycleStart = rootPos[0];
                _cycleEnd = rootPos[last] + (rootPos[last] - rootPos[last - 1]) * remaining;
                float endYaw = Yaw(rootFwd[last]) + Mathf.DeltaAngle(Yaw(rootFwd[last - 1]), Yaw(rootFwd[last])) * remaining;
                _cycleTurn = Quaternion.AngleAxis(Mathf.DeltaAngle(Yaw(rootFwd[0]), endYaw), Vector3.up);
            }

            public Vector3 Root(int index) => CyclePoint(_rootPos[Resolve(index, out int cycle)], cycle);
            public Vector3 Forward(int index) => CycleDirection(_rootFwd[Resolve(index, out int cycle)], cycle);
            public Vector3 Bone(int index, int bone) => CyclePoint(_bonePos[Resolve(index, out int cycle) * BoneCount + bone], cycle);

            /// <summary>Clip time of a frame index, continuous across cycles for a looping clip.</summary>
            public float TimeOf(int index)
            {
                int frame = Resolve(index, out int cycle);
                return cycle * _length + frame * _dt;
            }

            private int Resolve(int index, out int cycle)
            {
                cycle = 0;
                if (!Looping) return Mathf.Clamp(index, 0, FrameCount - 1);
                while (index < 0) { index += FrameCount; cycle--; }
                while (index >= FrameCount) { index -= FrameCount; cycle++; }
                return index;
            }

            private Vector3 CyclePoint(Vector3 point, int cycle)
            {
                for (; cycle > 0; cycle--) point = _cycleTurn * (point - _cycleStart) + _cycleEnd;
                for (; cycle < 0; cycle++) point = Quaternion.Inverse(_cycleTurn) * (point - _cycleEnd) + _cycleStart;
                return point;
            }

            private Vector3 CycleDirection(Vector3 direction, int cycle)
            {
                for (; cycle > 0; cycle--) direction = _cycleTurn * direction;
                for (; cycle < 0; cycle++) direction = Quaternion.Inverse(_cycleTurn) * direction;
                return direction;
            }

            private static float Yaw(Vector3 forward) => Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        /// <summary>Flags grounded feet: low world height and near-zero world speed.</summary>
        private static byte[] ExtractContacts(ClipSamples samples, int[] contactBones)
        {
            int frameCount = samples.FrameCount;
            int boneCount = samples.BoneCount;
            var contacts = new byte[frameCount];
            if (contactBones == null || contactBones.Length == 0) return contacts;

            for (int f = 0; f < frameCount; f++)
            {
                float span = Mathf.Max(samples.TimeOf(f + 1) - samples.TimeOf(f - 1), 1e-5f);

                byte mask = 0;
                for (int c = 0; c < contactBones.Length && c < 8; c++)
                {
                    int b = contactBones[c];
                    if (b < 0 || b >= boneCount) continue;
                    Vector3 pos = samples.Bone(f, b);
                    float speed = (samples.Bone(f + 1, b) - samples.Bone(f - 1, b)).magnitude / span;
                    if (speed <= ContactMaxSpeed && pos.y <= ContactMaxHeight)
                        mask |= (byte)(1 << c);
                }
                contacts[f] = mask;
            }
            return contacts;
        }

        #endregion

        #region Tools and Utilities

        private static void WriteTrajectory(
            float[] features, int rowOffset, FeatureSchema schema, CharacterSpace space,
            ClipSamples samples, int f, float[] times, int fps)
        {
            int posOffset = rowOffset + schema.TrajectoryPositionOffset;
            int dirOffset = rowOffset + schema.TrajectoryDirectionOffset;

            for (int p = 0; p < times.Length; p++)
            {
                int j = f + Mathf.RoundToInt(times[p] * fps);
                Vector2 localPos = space.ToLocalPoint(samples.Root(j));
                Vector2 localDir = space.ToLocalDirection(samples.Forward(j));

                features[posOffset + p * 2] = localPos.x;
                features[posOffset + p * 2 + 1] = localPos.y;
                features[dirOffset + p * 2] = localDir.x;
                features[dirOffset + p * 2 + 1] = localDir.y;
            }
        }

        private static void WriteBones(
            float[] features, int rowOffset, FeatureSchema schema, CharacterSpace space,
            ClipSamples samples, int f)
        {
            int posOffset = rowOffset + schema.BonePositionOffset;
            int velOffset = rowOffset + schema.BoneVelocityOffset;

            float span = Mathf.Max(samples.TimeOf(f + 1) - samples.TimeOf(f - 1), 1e-5f);

            bool naive = schema.PoseMode == PoseCostMode.Naive;

            for (int b = 0; b < samples.BoneCount; b++)
            {
                Vector3 localPos = space.ToLocalOffset3D(samples.Bone(f, b));
                Vector3 worldVel = (samples.Bone(f + 1, b) - samples.Bone(f - 1, b)) / span;
                Vector3 localVel = space.ToLocalVector3D(worldVel);

                // Through the schema, never inline: the live query writes the same slots from the
                // same method, and a formula that drifted between the two would not look like a bug -
                // it would look like the character simply matching badly.
                Vector3 pose = schema.BonePoseValue(localPos, localVel);
                features[posOffset + b * 3] = pose.x;
                features[posOffset + b * 3 + 1] = pose.y;
                features[posOffset + b * 3 + 2] = pose.z;

                if (!naive)
                    continue; // the composite already carries the velocity; the group is empty.

                features[velOffset + b * 3] = localVel.x;
                features[velOffset + b * 3 + 1] = localVel.y;
                features[velOffset + b * 3 + 2] = localVel.z;
            }
        }

        private static void WriteRootVelocity(
            float[] features, int rowOffset, FeatureSchema schema, CharacterSpace space,
            ClipSamples samples, int f)
        {
            float span = Mathf.Max(samples.TimeOf(f + 1) - samples.TimeOf(f - 1), 1e-5f);

            Vector3 worldVel = (samples.Root(f + 1) - samples.Root(f - 1)) / span;
            Vector2 localVel = space.ToLocalDirection(worldVel);

            int offset = rowOffset + schema.RootVelocityOffset;
            features[offset] = localVel.x;
            features[offset + 1] = localVel.y;
        }

        #endregion
    }
}
