using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Kinema.MotionMatching.Editor
{
    /// <summary>
    /// Replaces a matched character's rig in one step: instantiate the new model at the same spot,
    /// copy every component (values and asset references included) from the old character onto it,
    /// re-point what referred to the old body - the controller's Animator, anything following the
    /// old transform - and delete the old character.
    ///
    /// Component copying goes through ComponentUtility so this works for any script the project
    /// added, not just Kinema's. Edit mode only: in play mode half the copied state would be live
    /// runtime state that does not survive a transplant.
    /// </summary>
    public static class RigSwapUtility
    {
        #region Main API

        /// <summary>True when the swap can run at all; <paramref name="reason"/> says why not.</summary>
        public static bool CanSwap(MotionMatchingController controller, GameObject newRig, out string reason)
        {
            reason = null;
            if (Application.isPlaying) { reason = "Rig swapping is edit-mode only."; return false; }
            if (controller == null) { reason = "No character selected."; return false; }
            if (newRig == null) { reason = "Assign a rig prefab or model."; return false; }
            if (newRig.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
            { reason = $"'{newRig.name}' has no skinned mesh - it would be invisible."; return false; }
            return true;
        }

        public static GameObject Swap(MotionMatchingController controller, GameObject newRig)
        {
            if (!CanSwap(controller, newRig, out string reason))
            {
                Debug.LogError("[Kinema] " + reason);
                return null;
            }

            GameObject old = controller.gameObject;
            var replacement = (GameObject)PrefabUtility.InstantiatePrefab(newRig);
            if (replacement == null) replacement = Object.Instantiate(newRig);
            Undo.RegisterCreatedObjectUndo(replacement, "Swap Character Rig");

            replacement.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
            replacement.transform.SetParent(old.transform.parent, true);

            CopyComponents(old, replacement);
            RewireAnimator(replacement);
            RetargetReferences(old.transform, replacement.transform);

            string name = old.name;
            Undo.DestroyObjectImmediate(old);
            replacement.name = name;
            Selection.activeGameObject = replacement;

            Debug.Log($"[Kinema] Rig swapped to '{newRig.name}'. Same database, same components - " +
                      "Humanoid retargeting maps the data onto the new body.");
            return replacement;
        }

        #endregion

        #region Tools and Utilities

        /// <summary>
        /// Copies every root component the new rig does not already own, in declaration order so
        /// RequireComponent chains (controller before its dependents) stay satisfied.
        /// </summary>
        private static void CopyComponents(GameObject from, GameObject to)
        {
            foreach (Component component in from.GetComponents<Component>())
            {
                if (component is Transform) continue;
                System.Type type = component.GetType();

                // The rig usually ships its own Animator; keep it rather than cloning a stale one.
                if (component is Animator) continue;

                ComponentUtility.CopyComponent(component);
                Component existing = to.GetComponent(type);
                if (existing != null && type != typeof(CharacterController))
                    ComponentUtility.PasteComponentValues(existing);
                else
                    ComponentUtility.PasteComponentAsNew(to);
            }
        }

        private static void RewireAnimator(GameObject character)
        {
            var animator = character.GetComponent<Animator>();
            if (animator == null) animator = Undo.AddComponent<Animator>(character);
            animator.applyRootMotion = true;

            var controller = character.GetComponent<MotionMatchingController>();
            if (controller == null) return;
            var so = new SerializedObject(controller);
            SerializedProperty prop = so.FindProperty("_animator");
            if (prop != null)
            {
                prop.objectReferenceValue = animator;
                so.ApplyModifiedProperties();
            }
        }

        /// <summary>
        /// Anything in the scene holding the old transform (follow cameras, AI targets) follows the
        /// new one.
        ///
        /// Recorded for undo, unlike the rest of the swap's bookkeeping. Ctrl+Z restores the old
        /// character and destroys the replacement; if these repoints were not undone with it, every
        /// camera and AI target would still be aimed at the destroyed replacement - a scene worse off
        /// than before the swap, and one the user cannot get back by hand.
        /// </summary>
        private static void RetargetReferences(Transform oldTransform, Transform newTransform)
        {
            foreach (MonoBehaviour behaviour in UnityCompat.FindAll<MonoBehaviour>())
            {
                if (behaviour == null || behaviour.transform.IsChildOf(newTransform)) continue;

                var so = new SerializedObject(behaviour);
                SerializedProperty prop = so.GetIterator();
                bool changed = false;
                while (prop.NextVisible(true))
                {
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (!ReferenceEquals(prop.objectReferenceValue, oldTransform)) continue;
                    prop.objectReferenceValue = newTransform;
                    changed = true;
                }
                if (changed) so.ApplyModifiedProperties();
            }
        }

        #endregion
    }
}
