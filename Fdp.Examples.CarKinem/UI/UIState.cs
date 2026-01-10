using CarKinem.Core;
using CarKinem.Trajectory;
using CarKinem.Formation;

namespace Fdp.Examples.CarKinem.UI
{
    /// <summary>
    /// Shared UI state for controls and inputs.
    /// </summary>
    public class UIState
    {
        public VehicleClass SelectedVehicleClass { get; set; } = VehicleClass.PersonalCar;
        public global::CarKinem.Trajectory.TrajectoryInterpolation InterpolationMode { get; set; } = global::CarKinem.Trajectory.TrajectoryInterpolation.CatmullRom;
        public FormationType SelectedFormationType { get; set; } = FormationType.Column;
    }
}
