namespace CarKinem.Core
{
    /// <summary>
    /// Vehicle classification for different sizes and characteristics.
    /// </summary>
    public enum VehicleClass
    {
        PersonalCar = 0,  // Standard sedan/car
        Truck = 1,        // Delivery truck
        Bus = 2,          // Large bus
        Tank = 3,         // Military tank (slow, heavy, small turn radius)
        Pedestrian = 4    // Person on foot (very small, tight turns)
    }
    
    /// <summary>
    /// Preset configurations for different vehicle classes.
    /// </summary>
    public static class VehiclePresets
    {
        public static VehicleParams GetPreset(VehicleClass vehicleClass)
        {
            return vehicleClass switch
            {
                VehicleClass.PersonalCar => new VehicleParams
                {
                    Length = 4.5f,
                    Width = 2.0f,
                    WheelBase = 2.7f,
                    MaxSpeedFwd = 25.0f,
                    MaxAccel = 3.0f,
                    MaxDecel = 6.0f,
                    MaxSteerAngle = 0.6f,
                    MaxSteerRate = 1.0f,
                    MaxLatAccel = 8.0f,
                    AvoidanceRadius = 3.0f,
                    LookaheadTimeMin = 0.5f,
                    LookaheadTimeMax = 2.0f,
                    AccelGain = 2.0f
                },
                
                VehicleClass.Truck => new VehicleParams
                {
                    Length = 8.0f,
                    Width = 2.5f,
                    WheelBase = 5.0f,
                    MaxSpeedFwd = 18.0f,
                    MaxAccel = 1.5f,
                    MaxDecel = 4.0f,
                    MaxSteerAngle = 0.4f,
                    MaxSteerRate = 0.6f,
                    MaxLatAccel = 5.0f,
                    AvoidanceRadius = 5.0f,
                    LookaheadTimeMin = 1.0f,
                    LookaheadTimeMax = 3.0f,
                    AccelGain = 1.5f
                },
                
                VehicleClass.Bus => new VehicleParams
                {
                    Length = 12.0f,
                    Width = 2.8f,
                    WheelBase = 7.0f,
                    MaxSpeedFwd = 15.0f,
                    MaxAccel = 1.2f,
                    MaxDecel = 3.5f,
                    MaxSteerAngle = 0.35f,
                    MaxSteerRate = 0.5f,
                    MaxLatAccel = 4.0f,
                    AvoidanceRadius = 7.0f,
                    LookaheadTimeMin = 1.5f,
                    LookaheadTimeMax = 4.0f,
                    AccelGain = 1.2f
                },
                
                VehicleClass.Tank => new VehicleParams
                {
                    Length = 7.0f,
                    Width = 3.5f,
                    WheelBase = 4.5f,
                    MaxSpeedFwd = 12.0f,
                    MaxAccel = 2.0f,
                    MaxDecel = 4.0f,
                    MaxSteerAngle = 0.8f,  // Can turn sharply (tracks)
                    MaxSteerRate = 1.2f,
                    MaxLatAccel = 6.0f,
                    AvoidanceRadius = 5.0f,
                    LookaheadTimeMin = 0.8f,
                    LookaheadTimeMax = 2.5f,
                    AccelGain = 1.8f
                },
                
                VehicleClass.Pedestrian => new VehicleParams
                {
                    Length = 0.6f,
                    Width = 0.4f,
                    WheelBase = 0.3f,
                    MaxSpeedFwd = 2.0f,  // Walking speed
                    MaxAccel = 1.0f,
                    MaxDecel = 2.0f,
                    MaxSteerAngle = 1.57f,  // Nearly 90 degrees (can turn in place)
                    MaxSteerRate = 3.0f,
                    MaxLatAccel = 3.0f,
                    AvoidanceRadius = 0.8f,
                    LookaheadTimeMin = 0.3f,
                    LookaheadTimeMax = 1.0f,
                    AccelGain = 2.5f
                },
                
                _ => GetPreset(VehicleClass.PersonalCar) // Default
            };
        }
        
        public static (byte R, byte G, byte B) GetColor(VehicleClass vehicleClass)
        {
            return vehicleClass switch
            {
                VehicleClass.PersonalCar => (200, 100, 100),    // Red
                VehicleClass.Truck => (100, 150, 200),          // Blue
                VehicleClass.Bus => (200, 200, 100),            // Yellow
                VehicleClass.Tank => (100, 100, 100),           // Gray
                VehicleClass.Pedestrian => (150, 200, 150),     // Green
                _ => (200, 100, 100)
            };
        }
    }
}
