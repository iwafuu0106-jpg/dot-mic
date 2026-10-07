namespace DotMic;

// Relative circular drag. Unwrap at ±180°, quantize detents, and never integrate through the centre.
internal sealed class RotaryGesture
{
    private double? previousAngle;
    private double value;
    private double origin;
    private double published;
    private bool fineMode;
    internal void Begin(double x, double y, double gain, bool fine = false)
    {
        value = origin = published = gain;
        fineMode = fine;
        previousAngle = Angle(x, y);
    }
    internal double Move(double x, double y, bool fine)
    {
        if (fine != fineMode)
        {
            // Rebase the detent grid when Shift changes, never snap backwards to a coarser grid.
            value = origin = published;
            fineMode = fine;
        }
        double? angle = Angle(x, y);
        if (angle.HasValue && previousAngle.HasValue)
        {
            double delta = angle.Value - previousAngle.Value;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            value = Math.Clamp(value + delta * 48 / 270 * (fine ? .2 : 1), -12, 36);
        }
        previousAngle = angle;
        double step = fine ? .1 : .5;
        published = value is -12 or 36 ? value : Math.Clamp(Math.Round(origin + Math.Round((value - origin) / step, MidpointRounding.AwayFromZero) * step, 1), -12, 36);
        return published;
    }
    private static double? Angle(double x, double y) => x * x + y * y < 144 ? null : Math.Atan2(x, -y) * 180 / Math.PI;
}
