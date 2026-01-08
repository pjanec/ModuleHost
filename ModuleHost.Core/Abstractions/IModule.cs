using System;
using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Represents a self-contained unit of simulation logic in the ModuleHost architecture.
    /// 
    /// <para><b>Two Execution Patterns:</b></para>
    /// 
    /// <para><b>Pattern 1: System-Based Modules (Recommended)</b></para>
    /// <list type="bullet">
    /// <item>Implement <see cref="RegisterSystems"/> to register IModuleSystem instances</item>
    /// <item>Leave <see cref="Tick"/> empty (kernel executes systems automatically)</item>
    /// <item>Better separation of concerns, testability, and phase control</item>
    /// <item>Example: GeographicTransformModule, EntityLifecycleModule</item>
    /// </list>
    /// 
    /// <code>
    /// public class MyModule : IModule
    /// {
    ///     public void RegisterSystems(ISystemRegistry registry)
    ///     {
    ///         registry.RegisterSystem(new MySystem());  // Kernel runs this
    ///     }
    ///     
    ///     public void Tick(ISimulationView view, float deltaTime)
    ///     {
    ///         // Empty - kernel executes registered systems
    ///     }
    /// }
    /// </code>
    /// 
    /// <para><b>Pattern 2: Direct Execution Modules</b></para>
    /// <list type="bullet">
    /// <item>Implement <see cref="Tick"/> with all module logic</item>
    /// <item><see cref="RegisterSystems"/> remains empty</item>
    /// <item>Simpler for self-contained modules without subsystems</item>
    /// <item>No phase control, all logic runs in module tick order</item>
    /// </list>
    /// 
    /// <code>
    /// public class SimpleModule : IModule
    /// {
    ///     public void Tick(ISimulationView view, float deltaTime)
    ///     {
    ///         // All logic here - runs when kernel calls module.Tick()
    ///         var query = view.Query().With&lt;MyComponent&gt;().Build();
    ///         foreach (var entity in query) { /* ... */ }
    ///     }
    /// }
    /// </code>
    /// 
    /// <para><b>Execution Flow:</b></para>
    /// <code>
    /// // Initialization (called once):
    /// kernel.RegisterModule(myModule);
    ///   → myModule.RegisterSystems(registry)  // Kernel collects systems
    /// 
    /// // Every frame:
    /// kernel.Tick(deltaTime)
    ///   → For each phase (Input, Simulation, PostSimulation, etc.):
    ///       → Execute all systems tagged with [UpdateInPhase(phase)]
    ///   → Then: myModule.Tick(view, deltaTime)  // Module custom logic
    /// </code>
    /// 
    /// <para><b>When to use which pattern:</b></para>
    /// <list type="table">
    /// <item>
    /// <term>System-Based</term>
    /// <description>Multiple subsystems, phase-specific logic, reactive scheduling</description>
    /// </item>
    /// <item>
    /// <term>Direct Execution</term>
    /// <description>Simple module, single responsibility, no phase requirements</description>
    /// </item>
    /// </list>
    /// 
    /// <para>See: FDP-ModuleHost-User-Guide.md#modules-modulehost for detailed examples</para>
    /// </summary>
    public interface IModule
    {
        /// <summary>
        /// Module name for diagnostics and logging.
        /// Must be unique within the kernel for proper identification.
        /// </summary>
        /// <example>"GeographicTransform", "EntityLifecycleManager", "PhysicsSimulation"</example>
        string Name { get; }
        
        /// <summary>
        /// Execution policy defining how and when this module runs.
        /// 
        /// <para><b>Options:</b></para>
        /// <list type="bullet">
        /// <item><c>ExecutionPolicy.Synchronous()</c> - Runs every frame, main thread</item>
        /// <item><c>ExecutionPolicy.FrameSynced(targetHz)</c> - Runs at reduced rate, main thread</item>
        /// <item><c>ExecutionPolicy.FastReplica()</c> - Runs every frame with live + snapshot data</item>
        /// <item><c>ExecutionPolicy.SlowBackground(targetHz)</c> - Runs async on background thread</item>
        /// </list>
        /// 
        /// <para>Replaces obsolete Tier + UpdateFrequency pattern.</para>
        /// </summary>
        /// <example>
        /// <code>
        /// // Fast, every frame:
        /// public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        /// 
        /// // Slow, 10Hz background:
        /// public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10);
        /// </code>
        /// </example>
        ExecutionPolicy Policy 
        {
            get
            {
                #pragma warning disable CS0618 // Type or member is obsolete
                // Fallback for legacy modules implementing Tier
                if (Tier == ModuleTier.Fast)
                {
                    return ExecutionPolicy.FastReplica();
                }
                else
                {
                    // Convert UpdateFrequency
                    // UpdateFrequency: 1 = 60Hz. 2 = 30Hz.
                    int freq = UpdateFrequency;
                    if (freq <= 0) freq = 1;
                    int targetHz = 60 / freq;
                    return ExecutionPolicy.SlowBackground(targetHz);
                }
                #pragma warning restore CS0618
            }
        }
        
        /// <summary>
        /// Register subsystems that should be executed by the kernel.
        /// Called once during module initialization (kernel.RegisterModule).
        /// 
        /// <para><b>System-Based Module Pattern:</b></para>
        /// <list type="number">
        /// <item>Create IModuleSystem instances (with phase attributes)</item>
        /// <item>Register them via registry.RegisterSystem()</item>
        /// <item>Kernel executes systems in phase order automatically</item>
        /// <item>Leave Tick() empty</item>
        /// </list>
        /// 
        /// <para><b>Benefits:</b></para>
        /// <list type="bullet">
        /// <item>Systems can specify execution phase ([UpdateInPhase])</item>
        /// <item>Better testability (mock systems independently)</item>
        /// <item>Cleaner separation of concerns</item>
        /// <item>Supports reactive scheduling (WatchComponents, WatchEvents)</item>
        /// </list>
        /// 
        /// <para>If module uses direct execution pattern, leave empty (default implementation).</para>
        /// </summary>
        /// <param name="registry">System registry to register subsystems with</param>
        /// <example>
        /// <code>
        /// public void RegisterSystems(ISystemRegistry registry)
        /// {
        ///     // Register systems - kernel will execute them in phase order
        ///     registry.RegisterSystem(new NetworkSmoothingSystem(_transform));    // Input phase
        ///     registry.RegisterSystem(new CoordinateTransformSystem(_transform));  // PostSim phase
        ///     
        ///     // Store references if module needs to access them in Tick()
        ///     _systems.Add(smoothingSystem);
        /// }
        /// </code>
        /// </example>
        void RegisterSystems(ISystemRegistry registry) { }
        
        /// <summary>
        /// Main module execution method. Called by kernel every frame (or at Policy frequency).
        /// 
        /// <para><b>Two Usage Patterns:</b></para>
        /// 
        /// <para><b>Pattern 1: Empty (System-Based Modules)</b></para>
        /// <list type="bullet">
        /// <item>If module uses RegisterSystems(), leave Tick() empty</item>
        /// <item>Kernel executes registered systems automatically in phase order</item>
        /// <item>Module.Tick() called AFTER all system phases complete</item>
        /// <item>Use for coordination logic that needs to run after systems</item>
        /// </list>
        /// 
        /// <code>
        /// // System-based module:
        /// public void Tick(ISimulationView view, float deltaTime)
        /// {
        ///     // Empty - systems handle all logic
        ///     // OR: Optional coordination logic after systems complete
        /// }
        /// </code>
        /// 
        /// <para><b>Pattern 2: Direct Implementation</b></para>
        /// <list type="bullet">
        /// <item>Implement all module logic directly in Tick()</item>
        /// <item>No RegisterSystems() needed</item>
        /// <item>Simpler for self-contained modules</item>
        /// <item>No phase control</item>
        /// </list>
        /// 
        /// <code>
        /// // Direct execution module:
        /// public void Tick(ISimulationView view, float deltaTime)
        /// {
        ///     // Query entities
        ///     foreach (var entity in _query)
        ///     {
        ///         // Process entity
        ///         var component = view.GetComponentRO&lt;MyComponent&gt;(entity);
        ///         // ...
        ///     }
        /// }
        /// </code>
        /// 
        /// <para><b>Execution Order (each frame):</b></para>
        /// <code>
        /// 1. Kernel executes all [UpdateInPhase(Input)] systems
        /// 2. Kernel executes all [UpdateInPhase(Simulation)] systems
        /// 3. Kernel executes all [UpdateInPhase(PostSimulation)] systems
        /// 4. (etc. for all phases)
        /// 5. Kernel calls module.Tick() for each module
        /// </code>
        /// 
        /// <para><b>Important:</b> Systems run BEFORE module.Tick(). Use Tick() for:</para>
        /// <list type="bullet">
        /// <item>Coordination logic that needs results from all systems</item>
        /// <item>Custom logic that doesn't fit a specific phase</item>
        /// <item>Direct execution modules (no systems)</item>
        /// </list>
        /// </summary>
        /// <param name="view">Read-only view of the simulation state</param>
        /// <param name="deltaTime">Time elapsed since last tick (seconds)</param>
        /// <example>
        /// <code>
        /// // Example: Module.Tick() used for summary statistics after systems complete
        /// public void Tick(ISimulationView view, float deltaTime)
        /// {
        ///     // Systems already executed - now gather statistics
        ///     _totalEntities = view.Query().With&lt;NetworkOwnership&gt;().Build().Count();
        ///     _ownedCount = view.Query().With&lt;NetworkOwnership&gt;().Build()
        ///         .Count(e => view.GetComponentRO&lt;NetworkOwnership&gt;(e).PrimaryOwnerId == _localNodeId);
        /// }
        /// </code>
        /// </example>
        void Tick(ISimulationView view, float deltaTime);
        
        /// <summary>
        /// Component types to watch for changes (reactive scheduling).
        /// 
        /// <para>If specified, module only executes when watched components change.</para>
        /// <para>Reduces CPU usage for modules that don't need to run every frame.</para>
        /// 
        /// <para><b>Example Use Cases:</b></para>
        /// <list type="bullet">
        /// <item>UI module: Only update when DisplayName or Health changes</item>
        /// <item>Pathfinding: Only recompute when Position or NavTarget changes</item>
        /// </list>
        /// 
        /// <para>Return null or empty for always-execute behavior (default).</para>
        /// </summary>
        /// <example>
        /// <code>
        /// // Only run when Health or Velocity changes
        /// public IReadOnlyList&lt;Type&gt;? WatchComponents => new[]
        /// {
        ///     typeof(Health),
        ///     typeof(Velocity)
        /// };
        /// </code>
        /// </example>
        IReadOnlyList<Type>? WatchComponents => null;
        
        /// <summary>
        /// Event types to watch for firing (reactive scheduling).
        /// 
        /// <para>If specified, module executes when watched events are published.</para>
        /// <para>Enables event-driven architecture instead of polling.</para>
        /// 
        /// <para><b>Example Use Cases:</b></para>
        /// <list type="bullet">
        /// <item>Lifecycle module: React to ConstructionAck, DestructionAck</item>
        /// <item>Damage module: React to CollisionEvent, ExplosionEvent</item>
        /// </list>
        /// </summary>
        /// <example>
        /// <code>
        /// // Only run when these events fire
        /// public IReadOnlyList&lt;Type&gt;? WatchEvents => new[]
        /// {
        ///     typeof(ConstructionAck),
        ///     typeof(DestructionAck)
        /// };
        /// </code>
        /// </example>
        IReadOnlyList<Type>? WatchEvents => null;
        
        /// <summary>
        /// Components this module reads. Used for convoy snapshot filtering.
        /// 
        /// <para><b>Background:</b> FastReplica and SlowBackground modules receive</para>
        /// <para>snapshots of simulation state. Specifying required components reduces</para>
        /// <para>snapshot size by excluding irrelevant data.</para>
        /// 
        /// <para><b>Return Values:</b></para>
        /// <list type="bullet">
        /// <item><c>null</c> or empty: Include ALL components (safe but large snapshots)</item>
        /// <item>Specific types: Include only these components (optimized)</item>
        /// </list>
        /// 
        /// <para><b>Performance Impact:</b></para>
        /// <list type="bullet">
        /// <item>100 component types, module needs 5: 95% snapshot size reduction</item>
        /// <item>Faster serialization, less memory, reduced convoy contention</item>
        /// </list>
        /// </summary>
        /// <returns>Component types required by this module, or null for all</returns>
        /// <example>
        /// <code>
        /// // Pathfinding module only needs position and navigation
        /// public IEnumerable&lt;Type&gt;? GetRequiredComponents() => new[]
        /// {
        ///     typeof(Position),
        ///     typeof(NavTarget),
        ///     typeof(NavAgent)
        /// };
        /// </code>
        /// </example>
        IEnumerable<Type>? GetRequiredComponents() => null;
        
        // ============================================================
        // DEPRECATED (Kept for backward compatibility)
        // ============================================================
        
        /// <summary>
        /// [OBSOLETE] Use Policy.Mode instead.
        /// 
        /// <para>Legacy execution tier classification:</para>
        /// <list type="bullet">
        /// <item>Fast: Runs synchronously every frame</item>
        /// <item>Slow: Runs asynchronously at reduced rate</item>
        /// </list>
        /// 
        /// <para>Replaced by ExecutionPolicy for finer control.</para>
        /// </summary>
        [Obsolete("Use Policy.Mode and Policy.Strategy instead. Will be removed in v2.0.")]
        ModuleTier Tier 
        {
            get
            {
                return Policy.Mode == RunMode.Synchronous || Policy.Mode == RunMode.FrameSynced
                    ? ModuleTier.Fast
                    : ModuleTier.Slow;
            }
        }
        
        /// <summary>
        /// [OBSOLETE] Use Policy.TargetFrequencyHz instead.
        /// 
        /// <para>Legacy frequency control: 1 = 60Hz, 2 = 30Hz, 3 = 20Hz, etc.</para>
        /// <para>Formula: UpdateFrequency = 60 / desiredHz</para>
        /// 
        /// <para>Replaced by ExecutionPolicy.TargetFrequencyHz for clarity.</para>
        /// </summary>
        [Obsolete("Use Policy.TargetFrequencyHz instead. Will be removed in v2.0.")]
        int UpdateFrequency
        {
            get
            {
                if (Policy.TargetFrequencyHz == 0 || Policy.TargetFrequencyHz >= 60)
                    return 1; // Every frame
                
                return 60 / Policy.TargetFrequencyHz; // Convert Hz to frame count
            }
        }
    }
    
    /// <summary>
    /// [OBSOLETE] Module execution tier. Use ExecutionPolicy instead.
    /// 
    /// <para>Fast: Main thread, every frame, real-time</para>
    /// <para>Slow: Background thread, reduced rate, async</para>
    /// </summary>
    [Obsolete("Use ExecutionPolicy instead. Will be removed in v2.0.")]
    public enum ModuleTier
    {
        /// <summary>Synchronous execution every frame</summary>
        Fast,
        /// <summary>Asynchronous background execution</summary>
        Slow
    }
}
