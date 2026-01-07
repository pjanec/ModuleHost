# BATCH 08: Geographic Transform Services

**Batch ID:** BATCH-08  
**Phase:** Advanced - Coordinate Transformation  
**Priority:** LOW (P3)  
**Estimated Effort:** 4 days  
**Dependencies:** BATCH-07 (needs network components)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 8 (Geographic Transform)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 08 section
4. **BATCH-07 Review:** `../reviews/BATCH-07-REVIEW.md` (network components context)

---

## 🎯 Batch Objectives

### Primary Goal
Provide bidirectional transformation between FDP's Cartesian physics (XYZ) and network's Geodetic coordinates (Lat/Lon/Alt).

### Success Criteria
- ✅ Transform Lat/Lon/Alt ↔ Local XYZ working accurately
- ✅ Support dead reckoning and smoothing for remote entities
- ✅ Dual representation: Position (physics) + PositionGeodetic (network)
- ✅ Authority check: only sync direction we own
- ✅ All tests passing

### Why This Matters
Physics runs in flat Cartesian space for precision. Network descriptors use Geodetic coordinates for worldwide interoperability. This batch bridges the two, maintaining physics accuracy while enabling global federation.

---

## 📋 Tasks

### Task 8.1: Geographic Transform Service ⭐⭐

**Objective:** Implement WGS84 ↔ Local Tangent Plane transformation.

**What to Create:**

```csharp
// File: ModuleHost.Core/Geographic/IGeographicTransform.cs (NEW)

using System.Numerics;

namespace ModuleHost.Core.Geographic
{
    /// <summary>
    /// Transforms between geodetic (Lat/Lon/Alt) and local Cartesian (XYZ) coordinates.
    /// </summary>
    public interface IGeographicTransform
    {
        /// <summary>
        /// Set tangent plane origin for local coordinate system.
        /// </summary>
        void SetOrigin(double latDeg, double lonDeg, double altMeters);
        
        /// <summary>
        /// Convert geodetic position to local Cartesian (meters from origin).
        /// </summary>
        Vector3 ToCartesian(double latDeg, double lonDeg, double altMeters);
        
        /// <summary>
        /// Convert local Cartesian to geodetic position.
        /// </summary>
        (double lat, double lon, double alt) ToGeodetic(Vector3 localPos);
    }
    
    /// <summary>
    /// WGS84 ellipsoid implementation using East-North-Up tangent plane.
    /// Accurate for distances < 100km from origin.
    /// </summary>
    public class WGS84Transform : IGeographicTransform
    {
        private const double WGS84_A = 6378137.0; // Semi-major axis (m)
        private const double WGS84_F = 1.0 / 298.257223563; // Flattening
        private const double WGS84_E2 = WGS84_F * (2.0 - WGS84_F); // Eccentricity²
        
        private double _originLat;
        private double _originLon;
        private double _originAlt;
        private Matrix4x4 _ecefToLocal;
        private Matrix4x4 _localToEcef;
        
        public void SetOrigin(double latDeg, double lonDeg, double altMeters)
        {
            _originLat = latDeg * Math.PI / 180.0;
            _originLon = lonDeg * Math.PI / 180.0;
            _originAlt = altMeters;
            
            // Compute ECEF origin
            var originEcef = GeodeticToECEF(_originLat, _originLon, _originAlt);
            
            // Build rotation matrix (ENU)
            double sinLat = Math.Sin(_originLat);
            double cosLat = Math.Cos(_originLat);
            double sinLon = Math.Sin(_originLon);
            double cosLon = Math.Cos(_originLon);
            
            // ECEF → Local (ENU)
            _ecefToLocal = new Matrix4x4(
                -sinLon,              cosLon,             0, 0,
                -sinLat * cosLon,    -sinLat * sinLon,   cosLat, 0,
                 cosLat * cosLon,     cosLat * sinLon,   sinLat, 0,
                0, 0, 0, 1
            );
            
            Matrix4x4.Invert(_ecefToLocal, out _localToEcef);
        }
        
        public Vector3 ToCartesian(double latDeg, double lonDeg, double altMeters)
        {
            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            
            var ecef = GeodeticToECEF(lat, lon, altMeters);
            var originEcef = GeodeticToECEF(_originLat, _originLon, _originAlt);
            
            var delta = ecef - originEcef;
            return Vector3.Transform(delta, _ecefToLocal);
        }
        
        public (double lat, double lon, double alt) ToGeodetic(Vector3 localPos)
        {
            var originEcef = GeodeticToECEF(_originLat, _originLon, _originAlt);
            var deltaEcef = Vector3.Transform(localPos, _localToEcef);
            var ecef = originEcef + deltaEcef;
            
            return ECEFToGeodetic(ecef);
        }
        
        // WGS84 conversion helpers
        private Vector3 GeodeticToECEF(double lat, double lon, double alt)
        {
            double N = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
            double x = (N + alt) * Math.Cos(lat) * Math.Cos(lon);
            double y = (N + alt) * Math.Cos(lat) * Math.Sin(lon);
            double z = (N * (1.0 - WGS84_E2) + alt) * Math.Sin(lat);
            return new Vector3((float)x, (float)y, (float)z);
        }
        
        private (double, double, double) ECEFToGeodetic(Vector3 ecef)
        {
            // Iterative solution
            double x = ecef.X;
            double y = ecef.Y;
            double z = ecef.Z;
            
            double lon = Math.Atan2(y, x);
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1.0 - WGS84_E2));
            
            for (int i = 0; i < 5; i++)
            {
                double N = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
                double alt = p / Math.Cos(lat) - N;
                lat = Math.Atan2(z, p * (1.0 - WGS84_E2 * N / (N + alt)));
            }
            
            double N_final = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
            double alt_final = p / Math.Cos(lat) - N_final;
            
            return (lat * 180.0 / Math.PI, lon * 180.0 / Math.PI, alt_final);
        }
    }
}
```

**Unit Tests:**

```csharp
[Fact]
public void WGS84Transform_RoundTrip_PreservesCoordinates()
{
    var transform = new WGS84Transform();
    transform.SetOrigin(37.7749, -122.4194, 0); // San Francisco
    
    var local = transform.ToCartesian(37.8, -122.4, 100);
    var (lat, lon, alt) = transform.ToGeodetic(local);
    
    Assert.Equal(37.8, lat, precision: 6);
    Assert.Equal(-122.4, lon, precision: 6);
    Assert.Equal(100, alt, precision: 1);
}

[Fact]
public void WGS84Transform_Origin_ReturnsZero()
{
    var transform = new WGS84Transform();
    transform.SetOrigin(0, 0, 0); // Equator, Prime Meridian
    
    var local = transform.ToCartesian(0, 0, 0);
    
    Assert.Equal(Vector3.Zero, local);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Geographic/IGeographicTransform.cs`
- [ ] New file: `ModuleHost.Core/Geographic/WGS84Transform.cs`
- [ ] Unit tests: 2+

---

### Task 8.2: Coordinate Transform System ⭐⭐

**Objective:** Sync Position ↔ PositionGeodetic based on ownership.

**What to Create:**

```csharp
// File: ModuleHost.Core/Geographic/CoordinateTransformSystem.cs (NEW)

[UpdateInPhase(SystemPhase.PostSimulation)]
public class CoordinateTransformSystem : IModuleSystem
{
    private readonly IGeographicTransform _geo;
    
    public CoordinateTransformSystem(IGeographicTransform geo)
    {
        _geo = geo;
    }
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Outbound: Physics → Geodetic (for locally owned entities)
        var outbound = view.Query()
            .With<Position>()
            .With<PositionGeodetic>()
            .WithOwned<Position>() // Only entities we own
            .Build();
        
        foreach (var entity in outbound)
        {
            var localPos = view.GetComponentRO<Position>(entity);
            var geoPos = view.GetManagedComponentRO<PositionGeodetic>(entity);
            
            var (lat, lon, alt) = _geo.ToGeodetic(localPos.Value);
            
            // Only update if changed significantly
            if (Math.Abs(geoPos.Latitude - lat) > 1e-6 ||
                Math.Abs(geoPos.Longitude - lon) > 1e-6 ||
                Math.Abs(geoPos.Altitude - alt) > 0.1)
            {
                var newGeo = new PositionGeodetic
                {
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = alt
                };
                cmd.SetManagedComponent(entity, newGeo);
            }
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Geographic/CoordinateTransformSystem.cs`
- [ ] Tests: 3+

---

### Task 8.3: Network Smoothing System ⭐⭐

**Objective:** Interpolate remote entity positions.

**What to Create:**

```csharp
[UpdateInPhase(SystemPhase.Input)]
public class NetworkSmoothingSystem : IModuleSystem
{
    private readonly IGeographicTransform _geo;
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Inbound: Geodetic → Physics (for remote entities)
        var inbound = view.Query()
            .With<Position>()
            .With<PositionGeodetic>()
            .With<NetworkTarget>()
            .WithoutOwned<Position>() // Only remote entities
            .Build();
        
        foreach (var entity in inbound)
        {
            var geoPos = view.GetManagedComponentRO<PositionGeodetic>(entity);
            var target = view.GetComponentRO<NetworkTarget>(entity);
            var currentPos = view.GetComponentRO<Position>(entity);
            
            // Convert latest geodetic to Cartesian target
            var targetCartesian = _geo.ToCartesian(
                geoPos.Latitude, 
                geoPos.Longitude, 
                geoPos.Altitude);
            
            // Smooth interpolation (dead reckoning)
            float t = Math.Clamp(deltaTime * 10.0f, 0f, 1f);
            Vector3 newPos = Vector3.Lerp(currentPos.Value, targetCartesian, t);
            
            if (view is EntityRepository repo)
            {
                // Direct write optimization (main thread)
                ref var pos = ref repo.GetComponentRW<Position>(entity);
                pos.Value = newPos;
            }
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Geographic/NetworkSmoothingSystem.cs`
- [ ] Tests: 2+

---

### Task 8.4: Geographic Transform Module ⭐

**Objective:** Package systems into a module.

**What to Create:**

```csharp
public class GeographicTransformModule : IModule
{
    public string Name => "GeographicTransform";
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    private readonly IGeographicTransform _geo;
    
    public GeographicTransformModule(double originLat, double originLon, double originAlt)
    {
        _geo = new WGS84Transform();
        _geo.SetOrigin(originLat, originLon, originAlt);
    }
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new NetworkSmoothingSystem(_geo));
        registry.RegisterSystem(new CoordinateTransformSystem(_geo));
    }
    
    public void Tick(ISimulationView view, float deltaTime) { }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Geographic/GeographicTransformModule.cs`
- [ ] Integration test: 1

---

## ✅ Definition of Done

- [ ] All 4 tasks complete
- [ ] WGS84 transforms accurate
- [ ] Coordinate sync working
- [ ] Smoothing functional
- [ ] 8+ tests passing
- [ ] Report submitted

---

## 📊 Success Metrics

| Metric | Target |
|--------|--------|
| Transform accuracy | <1m error for 100km range |
| Smoothing latency | <100ms |
| Update rate |  60Hz |

---

## 🚧 Potential Challenges

### Challenge 1: Numerical Precision
**Issue:** Double vs float precision  
**Solution:** Use double for geo, float for physics  
**Ask if:** Precision errors visible

### Challenge 2: Coordinate System Handedness
**Issue:** ENU vs NED confusion  
**Solution:** Document coordinate frame clearly  
**Ask if:** Tests show flipped axes

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-08-REPORT.md`

---

Good luck! 🚀
