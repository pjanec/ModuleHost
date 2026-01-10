using Fdp.Kernel;
using System.Runtime.InteropServices;

namespace Fdp.Examples.CarKinem.Components
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleColor
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public VehicleColor(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static readonly VehicleColor Red = new VehicleColor(255, 0, 0);
        public static readonly VehicleColor Green = new VehicleColor(0, 255, 0);
        public static readonly VehicleColor Blue = new VehicleColor(50, 100, 255); // Road User Blue
        public static readonly VehicleColor Orange = new VehicleColor(255, 165, 0); // Roamer Orange
        public static readonly VehicleColor Cyan = new VehicleColor(0, 200, 255); // Formation Member
        public static readonly VehicleColor Magenta = new VehicleColor(255, 0, 255); // Leader
        public static readonly VehicleColor GreenYellow = new VehicleColor(173, 255, 47); // Standard
        public static readonly VehicleColor Gray = new VehicleColor(200, 200, 200);
    }
}
