using UnityEngine;
#if KINEMA_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kinema.MotionMatching.Samples
{
    /// <summary>
    /// The demo's controls, readable with or without the Input System package.
    ///
    /// With the package installed (<c>KINEMA_INPUT_SYSTEM</c>, set by the sample asmdef's version
    /// define) every control is an <c>InputAction</c> with keyboard, mouse and gamepad bindings -
    /// exactly what the demo always used. Without it the same controls read the legacy Input Manager.
    /// The sample used to reference the Input System unconditionally, so importing it into a project
    /// that does not carry the package - any built-in-input project, on any Unity version - was a
    /// wall of compile errors instead of a demo.
    /// </summary>
    public static class DemoInput
    {
        #region Main API

        /// <summary>WASD / arrows / left stick.</summary>
        public static DemoVector Move()
        {
#if KINEMA_INPUT_SYSTEM
            var action = new InputAction("Move", InputActionType.Value);
            action.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            action.AddBinding("<Gamepad>/leftStick");
            return new DemoVector(action);
#else
            return new DemoVector(LegacyMove);
#endif
        }

        /// <summary>Right stick, or the mouse while its middle button is held.</summary>
        public static DemoVector Orbit()
        {
#if KINEMA_INPUT_SYSTEM
            var action = new InputAction("Orbit", InputActionType.Value);
            action.AddBinding("<Gamepad>/rightStick");
            // Mouse delta only while the middle button is held, so plain mouse movement stays free.
            action.AddCompositeBinding("OneModifier")
                .With("Modifier", "<Mouse>/middleButton")
                .With("Binding", "<Mouse>/delta");
            return new DemoVector(action);
#else
            return new DemoVector(LegacyOrbit);
#endif
        }

        /// <summary>
        /// A button. <paramref name="bindings"/> are Input System control paths; <paramref name="legacyKeys"/>
        /// are what the same button reads when the package is absent (mouse buttons are KeyCodes too).
        /// </summary>
        public static DemoButton Button(string name, string[] bindings, params KeyCode[] legacyKeys)
        {
#if KINEMA_INPUT_SYSTEM
            var action = new InputAction(name, InputActionType.Button);
            foreach (string binding in bindings) action.AddBinding(binding);
            return new DemoButton(action);
#else
            return new DemoButton(legacyKeys);
#endif
        }

        #endregion

#if !KINEMA_INPUT_SYSTEM
        #region Tools and Utilities - legacy Input Manager

        // The default Input Manager defines Horizontal/Vertical (keys + first joystick) and
        // Mouse X/Y, but a project is free to delete them, and GetAxis throws on an unknown name.
        // Probe once; fall back to raw keys rather than throw every frame.
        private static bool? _hasMoveAxes, _hasMouseAxes;

        private static Vector2 LegacyMove()
        {
            Vector2 move;
            if (HasAxes(ref _hasMoveAxes, "Horizontal", "Vertical"))
            {
                move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            }
            else
            {
                move = new Vector2(
                    Key(KeyCode.D) + Key(KeyCode.RightArrow) - Key(KeyCode.A) - Key(KeyCode.LeftArrow),
                    Key(KeyCode.W) + Key(KeyCode.UpArrow) - Key(KeyCode.S) - Key(KeyCode.DownArrow));
                move = new Vector2(Mathf.Clamp(move.x, -1f, 1f), Mathf.Clamp(move.y, -1f, 1f));
            }
            // Same as the Input System's 2DVector composite: a diagonal is not faster than a straight line.
            return move.sqrMagnitude > 1f ? move.normalized : move;
        }

        private static Vector2 LegacyOrbit()
        {
            if (!Input.GetMouseButton(2) || !HasAxes(ref _hasMouseAxes, "Mouse X", "Mouse Y")) return Vector2.zero;
            // Mouse X/Y are pixels scaled by the axis sensitivity (0.1 by default); undo it so the
            // camera reads the same pixel delta the Input System path does.
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
        }

        private static float Key(KeyCode key) => Input.GetKey(key) ? 1f : 0f;

        private static bool HasAxes(ref bool? cache, string a, string b)
        {
            if (cache.HasValue) return cache.Value;
            try
            {
                Input.GetAxisRaw(a);
                Input.GetAxisRaw(b);
                cache = true;
            }
            catch (System.ArgumentException)
            {
                cache = false;
            }
            return cache.Value;
        }

        #endregion
#endif
    }

    /// <summary>A two-axis control from <see cref="DemoInput"/>.</summary>
    public sealed class DemoVector
    {
#if KINEMA_INPUT_SYSTEM
        private readonly InputAction _action;
        internal DemoVector(InputAction action) { _action = action; }
        public void Enable() => _action.Enable();
        public void Disable() => _action.Disable();
        public Vector2 ReadValue() => _action.ReadValue<Vector2>();
#else
        private readonly System.Func<Vector2> _read;
        internal DemoVector(System.Func<Vector2> read) { _read = read; }
        public void Enable() { }
        public void Disable() { }
        public Vector2 ReadValue() => _read();
#endif
    }

    /// <summary>A button from <see cref="DemoInput"/>.</summary>
    public sealed class DemoButton
    {
#if KINEMA_INPUT_SYSTEM
        private readonly InputAction _action;
        internal DemoButton(InputAction action) { _action = action; }
        public void Enable() => _action.Enable();
        public void Disable() => _action.Disable();
        public bool IsPressed() => _action.IsPressed();
        public bool WasPressedThisFrame() => _action.WasPressedThisFrame();
#else
        private readonly KeyCode[] _keys;
        internal DemoButton(KeyCode[] keys) { _keys = keys ?? System.Array.Empty<KeyCode>(); }
        public void Enable() { }
        public void Disable() { }

        public bool IsPressed()
        {
            foreach (KeyCode key in _keys)
                if (Input.GetKey(key)) return true;
            return false;
        }

        public bool WasPressedThisFrame()
        {
            foreach (KeyCode key in _keys)
                if (Input.GetKeyDown(key)) return true;
            return false;
        }
#endif
    }
}
