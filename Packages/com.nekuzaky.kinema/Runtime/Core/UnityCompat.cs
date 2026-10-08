using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// The handful of Unity APIs that were renamed between the oldest supported editor (2021.3 LTS)
    /// and Unity 6. Everything in the package goes through here instead of calling the version-specific
    /// name directly, so one #if per API is the whole cost of supporting every LTS.
    /// </summary>
    public static class UnityCompat
    {
        #region Public

        /// <summary>Every loaded, active object of type <typeparamref name="T"/>, in no particular order.</summary>
        public static T[] FindAll<T>() where T : Object
        {
#if UNITY_2022_2_OR_NEWER
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>The first loaded, active object of type <typeparamref name="T"/>, or null.</summary>
        public static T FindFirst<T>() where T : Object
        {
#if UNITY_2022_2_OR_NEWER
            return Object.FindFirstObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        #endregion
    }
}
