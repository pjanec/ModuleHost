namespace Fdp.Examples.BattleRoyale.Visualization;

using ModuleHost.Core; // For ModuleStats

public class ConsoleRenderer
{
    private int _lastFrame = 0;
    private float _lastTime = 0;
    
    public void Render(int frame, float time, int entityCount, 
        List<ModuleStats> moduleExecutions)
    {
        // Removed Console.Clear() to prevent crash in non-interactive environment
        // Console.Clear(); 
        Console.WriteLine("\n=== BattleRoyale Server Simulation ===");
        Console.WriteLine($"Frame: {frame} | Time: {time:F1}s | FPS: {CalculateFPS(frame, time):F1}");
        Console.WriteLine($"Entities: {entityCount}");
        Console.WriteLine("Module Executions (last second):");
        
        foreach (var stat in moduleExecutions.OrderByDescending(x => x.ExecutionCount))
        {
            var count = stat.ExecutionCount;
            var module = stat.ModuleName;
            var tier = count >= 59 ? "[FAST]" : "[SLOW]"; // >= 59 allows for slight off-by-one or startup
            Console.WriteLine($"  {tier} {module,-20} : {count,3} ticks");
        }
        
        _lastFrame = frame;
        _lastTime = time;
    }
    
    private float CalculateFPS(int frame, float time)
    {
        if (time - _lastTime < 0.01f) return 60.0f;
        return (frame - _lastFrame) / (time - _lastTime);
    }
}
