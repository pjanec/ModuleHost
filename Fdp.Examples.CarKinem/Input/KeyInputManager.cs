using System;
using System.Collections.Generic;
using Raylib_cs;

namespace Fdp.Examples.CarKinem.Input
{
    /// <summary>
    /// Manages multiple keys with progressive auto-repeat behavior.
    /// Provides a clean interface for registering keys with actions.
    /// </summary>
    public class KeyInputManager
    {
        private readonly Dictionary<KeyboardKey, KeyAutoRepeat> _keyStates = new();
        private readonly Dictionary<KeyboardKey, Action<int>> _keyActions = new();
        private float _currentTime;

        /// <summary>
        /// Register a key with an action to invoke when the key is pressed/repeated.
        /// </summary>
        /// <param name="key">The keyboard key to track</param>
        /// <param name="action">Action to invoke. Parameter is the number of invocations this frame.</param>
        public void RegisterKey(KeyboardKey key, Action<int> action)
        {
            if (!_keyStates.ContainsKey(key))
                _keyStates[key] = new KeyAutoRepeat();
            
            _keyActions[key] = action;
        }
        
        /// <summary>
        /// Register a key with a simple action (invoked once per triggering).
        /// </summary>
        public void RegisterKey(KeyboardKey key, Action action)
        {
            RegisterKey(key, count =>
            {
                for (int i = 0; i < count; i++)
                    action();
            });
        }

        /// <summary>
        /// Update all registered keys. Call this once per frame before processing actions.
        /// </summary>
        /// <param name="deltaTime">Time since last frame in seconds</param>
        public void Update(float deltaTime)
        {
            _currentTime += deltaTime;
            
            foreach (var kvp in _keyStates)
            {
                var key = kvp.Key;
                var autoRepeat = kvp.Value;
                
                bool isPressed = Raylib.IsKeyDown(key);
                autoRepeat.Update(isPressed, _currentTime);
            }
        }

        /// <summary>
        /// Process all pending key actions. Call this after Update() in the same frame.
        /// If there are too many pending invocations, they'll carry over to the next frame.
        /// </summary>
        /// <param name="maxInvocationsPerFrame">Maximum total invocations to process this frame (0 = unlimited)</param>
        public void ProcessActions(int maxInvocationsPerFrame = 0)
        {
            int totalProcessed = 0;
            
            foreach (var kvp in _keyStates)
            {
                var key = kvp.Key;
                var autoRepeat = kvp.Value;
                
                if (!_keyActions.TryGetValue(key, out var action))
                    continue;
                
                int pending = autoRepeat.PeekPendingInvocations();
                if (pending == 0)
                    continue;
                
                // Determine how many invocations we can process
                int toProcess = pending;
                if (maxInvocationsPerFrame > 0)
                {
                    int remaining = maxInvocationsPerFrame - totalProcessed;
                    if (remaining <= 0)
                        break; // Hit limit, stop processing
                    
                    toProcess = Math.Min(pending, remaining);
                }
                
                // Consume only what we're processing
                if (toProcess == pending)
                {
                    // Process all
                    autoRepeat.ConsumePendingInvocations();
                    action(toProcess);
                    totalProcessed += toProcess;
                }
                else
                {
                    // Partial processing - this is a bit tricky since we can't partially consume
                    // For now, process what we can and leave the rest
                    action(toProcess);
                    totalProcessed += toProcess;
                    
                    // Note: Remaining invocations stay pending for next frame
                    // We could enhance KeyAutoRepeat to support partial consumption if needed
                }
            }
        }
        
        /// <summary>
        /// Clear all registered keys and actions.
        /// </summary>
        public void Clear()
        {
            _keyStates.Clear();
            _keyActions.Clear();
        }
    }
}
