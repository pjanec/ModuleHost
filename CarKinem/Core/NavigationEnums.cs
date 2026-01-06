namespace CarKinem.Core
{
    /// <summary>
    /// Navigation mode enumeration.
    /// Determines how vehicle calculates its target.
    /// </summary>
    public enum NavigationMode : byte
    {
        None = 0,           // No active navigation (stationary or manual control)
        RoadGraph = 1,      // Follow road network (approach → follow → leave)
        CustomTrajectory = 2, // Follow custom trajectory from trajectory pool
        Formation = 3       // Follow formation target (overrides other modes)
    }

    /// <summary>
    /// Road graph state machine.
    /// Tracks progress through approach → follow → leave phases.
    /// </summary>
    public enum RoadGraphPhase : byte
    {
        Approaching = 0,    // Moving to closest entry point on road graph
        Following = 1,      // Following road segments
        Leaving = 2,        // Moving from road exit point to final destination
        Arrived = 3         // Reached final destination
    }
}
