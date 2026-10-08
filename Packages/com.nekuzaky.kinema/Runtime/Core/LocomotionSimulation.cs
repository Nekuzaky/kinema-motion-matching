using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// The maths of keeping the animation and the gameplay character in step - Holden's "code vs
    /// data driven displacement" (2021), the arrangement AAA motion matching ships with.
    ///
    /// Pure root motion (the data) never slides a foot, but the character only goes where some clip
    /// went: it answers the stick as late and as loosely as the capture allows. A simulated character
    /// (the code) answers the stick exactly, but an animation dragged along by it slides. The
    /// standard answer is both: simulate where the character should be, play root motion, and nudge
    /// the root motion toward the simulation by no more than a fraction of its own movement. The feet
    /// can slide at most by that fraction, and the character goes where it was asked to.
    ///
    /// The simulation uses the same critically damped spring, with the same response time, as
    /// <see cref="TrajectoryPredictor"/>'s <see cref="PredictionModel.CriticalSpring"/>: the
    /// character is pulled along exactly the trajectory the search was asked to match.
    /// </summary>
    public static class LocomotionSimulation
    {
        #region Main API

        /// <summary>
        /// Advances a critically damped spring on velocity toward <paramref name="desiredVelocity"/>
        /// by <paramref name="dt"/>, in closed form - exact for any step, so the result does not
        /// depend on frame rate. <paramref name="response"/> is the prediction's position response
        /// time (seconds): the spring's angular frequency is 2 / response.
        /// </summary>
        public static void Step(ref Vector3 position, ref Vector3 velocity, ref Vector3 acceleration,
                                Vector3 desiredVelocity, float response, float dt)
        {
            if (dt <= 0f) return;
            float y = 2f / Mathf.Max(response, 0.01f);
            Vector3 j0 = velocity - desiredVelocity;
            Vector3 j1 = acceleration + j0 * y;
            float eydt = Mathf.Exp(-y * dt);

            // Velocity error (j0 + j1 t) e^{-yt}, integrated for the position.
            position += desiredVelocity * dt
                        + j0 * ((1f - eydt) / y)
                        + j1 * ((1f - eydt * (1f + y * dt)) / (y * y));
            velocity = desiredVelocity + (j0 + j1 * dt) * eydt;
            acceleration = (acceleration - j1 * (y * dt)) * eydt;
        }

        /// <summary>
        /// The root-motion displacement for this frame, nudged toward the simulation.
        /// <paramref name="toSimulation"/> is the horizontal offset from where the animation alone
        /// would put the character to where the simulation is. The nudge closes that offset with
        /// the given half-life, but never by more than <paramref name="maxRatio"/> of the animation's
        /// own horizontal movement this frame - so a standing animation is never dragged (no
        /// sliding on the spot), and a moving one slides by at most that fraction.
        /// </summary>
        public static Vector3 Adjust(Vector3 animationDelta, Vector3 toSimulation, float halflife, float maxRatio, float dt)
        {
            if (dt <= 0f) return animationDelta;
            toSimulation.y = 0f;

            // Exact exponential decay of the offset over dt: frame-rate independent.
            Vector3 adjustment = toSimulation * (1f - Mathf.Exp(-0.69314718f * dt / Mathf.Max(halflife, 1e-4f)));

            float moved = new Vector2(animationDelta.x, animationDelta.z).magnitude;
            float limit = Mathf.Max(maxRatio, 0f) * moved;
            float length = adjustment.magnitude;
            if (length > limit) adjustment *= limit / length;

            return animationDelta + adjustment;
        }

        /// <summary>
        /// Keeps the simulation within <paramref name="maxLead"/> of the character, horizontally. A
        /// character stopped by a wall must not leave its simulation running on through it: the
        /// adjustment would then pull it into the wall for ever.
        /// </summary>
        public static Vector3 Leash(Vector3 simulation, Vector3 character, float maxLead)
        {
            Vector3 offset = simulation - character;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance > maxLead && distance > 1e-6f) offset *= maxLead / distance;
            return new Vector3(character.x + offset.x, character.y, character.z + offset.z);
        }

        /// <summary>
        /// A root-motion step as it would be if the clip were mirrored across the character's own
        /// sagittal plane (its local X flipped). <paramref name="deltaPosition"/> is world space, as
        /// <c>Animator.deltaPosition</c> is; <paramref name="deltaRotation"/> is the character-local
        /// step that <c>transform.rotation *= deltaRotation</c> applies, as <c>Animator.deltaRotation</c>
        /// is used. Root motion leaves the graph through the Animator unmirrored, whatever the pose
        /// job does to the bones, so a mirrored clip otherwise walks the body the original's way while
        /// its legs step the other.
        /// </summary>
        public static void Mirror(Quaternion characterRotation, ref Vector3 deltaPosition, ref Quaternion deltaRotation)
        {
            Vector3 local = Quaternion.Inverse(characterRotation) * deltaPosition;
            local.x = -local.x;
            deltaPosition = characterRotation * local;

            // A reflection maps a rotation's axis as a pseudo-vector: (x, y, z) -> (x, -y, -z).
            deltaRotation = new Quaternion(deltaRotation.x, -deltaRotation.y, -deltaRotation.z, deltaRotation.w);
        }

        #endregion
    }
}
