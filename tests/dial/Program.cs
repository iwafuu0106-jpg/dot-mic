using DotMic;

static (double x, double y) Point(double degrees)
{
    double radians = degrees * Math.PI / 180;
    return (70 * Math.Sin(radians), -70 * Math.Cos(radians));
}
static void Equal(double actual, double expected, string message)
{
    if (Math.Abs(actual - expected) > .001) throw new Exception($"{message}: expected {expected}, got {actual}");
}
var gesture = new RotaryGesture();
void Begin(double angle, double gain) { var p = Point(angle); gesture.Begin(p.x, p.y, gain); }
double Move(double angle, bool fine = false) { var p = Point(angle); return gesture.Move(p.x, p.y, fine); }

Begin(-67.5, 0); Equal(Move(-67.5), 0, "No gain change on click");
Equal(Move(-61.875), 1, "Clockwise moves +1 dB");
Equal(Move(-67.5), 0, "Counterclockwise reverses");
Begin(0, 2.4); Equal(Move(0), 2.4, "Direct-entry gain survives stationary drag");
Equal(Move(1), 2.4, "Detent deadband"); Equal(Move(3), 2.9, "Half-dB detent");
Begin(179, 0); Equal(Move(-179), .5, "Clockwise bottom crossing does not invert");
Begin(-179, 0); Equal(Move(179), -.5, "Counterclockwise bottom crossing does not invert");
Begin(-135, -12);
double last = -12;
for (int angle = -134; angle <= 135; angle++)
{
    double current = Move(angle);
    if (current < last || current - last > .51) throw new Exception("Sweep direction or jump regression");
    last = current;
}
Equal(last, 36, "Full 270-degree sweep");
Equal(Move(150), 36, "Upper end stop");
Equal(Move(144.375), 35, "Immediate reverse after end stop");
Begin(0, -12); Equal(Move(-20), -12, "Lower end stop"); Equal(Move(-14.375), -11, "Immediate reverse at lower stop");
Begin(0, 0); Equal(Move(5.625, true), .2, "Shift sensitivity");
Equal(Move(6, false), .2, "Releasing Shift does not snap backwards");
Equal(Move(8.8125), .7, "Coarse detents resume from the current fine value");
Begin(0, 0); Equal(gesture.Move(0, 0, false), 0, "Centre ignored"); Equal(Move(180), 0, "No jump after centre crossing");
Equal(Move(185.625), 1, "Drag resumes after centre");
Console.WriteLine("PASS production rotary gesture: direction, 270-degree sweep, angle wrap, detents, centre, fine input and end stops.");
