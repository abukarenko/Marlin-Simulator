using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;

namespace MarlinHost;

// Display-only estimate. Never sends G-code or changes printer state.
public sealed class MotionProfile
{
    public string Name { get; init; } = "Marlin Simulator";
    public Vector3 Minimum { get; init; }
    public Vector3 Maximum { get; init; } = new(220,220,250);
    public Vector3 MaxFeed { get; set; } = new(300,300,5);
    public Vector3 MaxAcceleration { get; set; } = new(3000,3000,100);
    public Vector3 HomingFeed { get; init; } = new(50,50,4);
    public Vector3 Bump { get; init; } = new(5,5,2);
    public Vector3 BumpDivisor { get; init; } = new(2,2,4);
    public double PrintAcceleration { get; set; } = 3000;
    public double TravelAcceleration { get; set; } = 3000;
    public double ExtruderFeed { get; set; } = 25;
    public float Raise { get; init; }
    public float AfterHome { get; init; }
    public bool SafeZ { get; init; }
    public Vector2 SafePoint { get; init; }
    public Vector3 ProbeOffset { get; set; }
    public float ProbeFast { get; init; } = 4;
    public float ProbeSlow { get; init; } = 3;
    public float ProbeClearance { get; init; } = 5;
    // Snapshot from the user's Configuration.h / Configuration_adv.h, 2026-11-14.
    // Separate from simulator defaults; live M503 overrides runtime limits/offsets.
    public static MotionProfile Ramps() => new()
    {
        Name="CoreXY Marlin 2.1.2.8 (Configuration.h)", Minimum=new(-11,-4,0), Maximum=new(210,210,160),
        MaxFeed=new(120,120,4), MaxAcceleration=new(2200,2200,90), HomingFeed=new(120,120,4.12f),
        Raise=11, AfterHome=5, SafeZ=true, SafePoint=new(105,105), ProbeOffset=new(0,24,0), ExtruderFeed=10
    };
}

public sealed class MotionPreview
{
    private readonly Func<double> clock;
    private ModalState modal = new();
    private readonly Queue<Step> steps = new();
    private Vector3 position, tail;
    private double tailTime;
    private int speed = 100, plane = 17;
    private bool homing;
    private int homedAxes;
    public MotionProfile Profile { get; }
    public int Pending => steps.Count;
    public bool Homing => homing;
    public Vector3 Planned => tail;
    public MotionPreview(MotionProfile profile, Func<double>? clock = null)
    {
        Profile=profile;
        this.clock=clock ?? (() => (double)Stopwatch.GetTimestamp()/Stopwatch.Frequency);
    }
    private sealed record Step(Vector3 From, Vector3 To, double Start, double Duration, double Ramp, double Cruise)
    {
        public double End => Start+Duration;
        public Vector3 At(double now)
        {
            double t=Math.Clamp(now-Start,0,Duration);
            double fraction;
            if (Ramp<=0) fraction=Duration>0 ? t/Duration : 1;
            else
            {
                double distance=Ramp+Cruise;
                fraction=t<Ramp ? t*t/(2*Ramp*distance) :
                    t<=Ramp+Cruise ? (t-Ramp/2)/distance : 1-Math.Pow(Duration-t,2)/(2*Ramp*distance);
            }
            return Vector3.Lerp(From,To,(float)Math.Clamp(fraction,0,1));
        }
    }
    public Vector3 Sample()
    {
        double now=clock();
        while(steps.TryPeek(out var done) && now>=done.End) { position=done.To; steps.Dequeue(); }
        return steps.TryPeek(out var current) ? current.At(now) : position;
    }
    private void Move(Vector3 target, double feed, bool printing=false, bool limit=true)
    {
        Sample();
        var delta=Vector3.Abs(target-tail); double length=Vector3.Distance(tail,target);
        if(length<1e-7) { tail=target; return; }
        double velocity=Math.Max(.001,feed), acceleration=printing ? Profile.PrintAcceleration : Profile.TravelAcceleration;
        for(int axis=0;axis<3;axis++) if(delta[axis]>.000001)
        {
            if(limit) velocity=Math.Min(velocity,Profile.MaxFeed[axis]*length/delta[axis]);
            acceleration=Math.Min(acceleration,Profile.MaxAcceleration[axis]*length/delta[axis]);
        }
        acceleration=Math.Max(.01,acceleration);
        double ramp=Math.Min(velocity/acceleration,Math.Sqrt(length/acceleration));
        double cruise=Math.Max(0,length/velocity-velocity/acceleration);
        double start=Math.Max(clock(),tailTime);
        steps.Enqueue(new(tail,target,start,2*ramp+cruise,ramp,cruise));
        tail=target; tailTime=start+2*ramp+cruise;
    }
    private void Hold(double seconds)
    {
        double start=Math.Max(clock(),tailTime);
        steps.Enqueue(new(tail,tail,start,Math.Max(0,seconds),0,Math.Max(0,seconds))); tailTime=start+Math.Max(0,seconds);
    }
    private void SetModalPosition(Vector3 p) => modal.Apply($"G92 X{GCode.N(p.X/(modal.Inches?25.4:1))} Y{GCode.N(p.Y/(modal.Inches?25.4:1))} Z{GCode.N(p.Z/(modal.Inches?25.4:1))}");
    public void Correct(Vector3 reported)
    {
        Sample();
        // Ordinary M114 reports the planner endpoint, not live stepper position.
        // Attach it after queued animation; never teleport to an ahead-of-time endpoint.
        if(homing) return;
        if(steps.Count>0) Move(reported,Math.Max(1,Profile.MaxFeed.X),limit:true);
        else { position=tail=reported; tailTime=clock(); }
        tail=reported; SetModalPosition(reported);
    }
    public void Stop()
    {
        position=Sample(); steps.Clear(); tail=position; tailTime=clock(); homing=false; SetModalPosition(position);
    }
    public void Started(string command)
    {
        string op=GCode.Op(command); var w=GCode.Words(command);
        if(op is "M203" or "M201" or "M204" or "M851") { ReadSettings(command); return; }
        if(op=="M220") { if(w.TryGetValue('S',out var s)) speed=Math.Clamp((int)s,1,1000); return; }
        if(op is "M410" or "M112") { Stop(); return; }
        if(op is "M18" or "M84") homedAxes=0;
        if(op=="G28") { Home(command,w); return; }
        if(op=="G4") { Hold(w.GetValueOrDefault('S',w.GetValueOrDefault('P')/1000)); return; }
        if(op is "G17" or "G18" or "G19") plane=int.Parse(op[1..]);
        var from=modal.Position; double e=modal.E, unit=modal.Inches?25.4:1;
        modal.Apply(command);
        if(op=="G92" && (w.ContainsKey('X') || w.ContainsKey('Y') || w.ContainsKey('Z')))
        {
            double at=Math.Max(clock(),tailTime); steps.Enqueue(new(tail,modal.Position,at,0,0,0)); tail=modal.Position;
        }
        if(op is "G0" or "G00" or "G1" or "G01" or "G2" or "G3")
        {
            double feed=modal.Feed/60*speed/100;
            if(op is "G2" or "G3")
            {
                var points=PrintJob.Arc(from,modal.Position,command,plane,unit);
                // Use a continuous nominal speed across tessellation points (no stop at each chord).
                foreach(var point in points) MoveArc(point,feed);
                if(points.Count==0) Move(modal.Position,feed,modal.E>e);
            }
            else
            {
                Move(modal.Position,feed,modal.E>e);
                if(from==modal.Position && modal.E!=e) Hold(Math.Abs(modal.E-e)/Math.Max(.001,Math.Min(feed,Profile.ExtruderFeed)));
            }
        }
    }
    private void MoveArc(Vector3 target,double feed)
    {
        double length=Vector3.Distance(tail,target); if(length<1e-8) return;
        var delta=Vector3.Abs(target-tail); double velocity=Math.Max(.001,feed);
        for(int i=0;i<3;i++) if(delta[i]>1e-7) velocity=Math.Min(velocity,Profile.MaxFeed[i]*length/delta[i]);
        double duration=length/velocity,start=Math.Max(clock(),tailTime);
        steps.Enqueue(new(tail,target,start,duration,0,duration)); tail=target; tailTime=start+duration;
    }
    private void Home(string command,Dictionary<char,double> w)
    {
        bool all=!command[3..].Any(c=>c is 'X' or 'Y' or 'Z');
        bool x=all||command[3..].Contains('X'), y=all||command[3..].Contains('Y'), z=all||command[3..].Contains('Z');
        if(z && Profile.SafeZ) { x|=(homedAxes&1)==0; y|=(homedAxes&2)==0; }
        homing=true;
        float raise=(float)w.GetValueOrDefault('R',Profile.Raise);
        if(raise>tail.Z) Move(tail with { Z=raise },Profile.HomingFeed.Z,limit:false);
        void Axis(int axis)
        {
            var target=tail; target[axis]=Profile.Minimum[axis];
            Move(target,Profile.HomingFeed[axis],limit:false);
            target[axis]+=Profile.Bump[axis]; Move(target,Profile.HomingFeed[axis],limit:false);
            target[axis]=Profile.Minimum[axis]; Move(target,Profile.HomingFeed[axis]/Profile.BumpDivisor[axis],limit:false);
        }
        if(x) Axis(0); if(y) Axis(1);
        if(z)
        {
            if(Profile.SafeZ)
            {
                Move(new(Profile.SafePoint.X-Profile.ProbeOffset.X,Profile.SafePoint.Y-Profile.ProbeOffset.Y,tail.Z),Profile.MaxFeed.X);
                var target=tail; target.Z=-Profile.ProbeOffset.Z;
                Move(target,Profile.ProbeFast,limit:false);
                target.Z+=Profile.ProbeClearance; Move(target,Profile.ProbeFast,limit:false);
                target.Z=-Profile.ProbeOffset.Z; Move(target,Profile.ProbeSlow,limit:false);
            }
            else Axis(2);
            if(Profile.AfterHome>0) Move(tail with { Z=Profile.AfterHome },Profile.HomingFeed.Z,limit:false);
        }
        SetModalPosition(tail);
        if(x) homedAxes|=1; if(y) homedAxes|=2; if(z) homedAxes|=4;
    }
    public void Completed(string command,string[] replies)
    {
        string op=GCode.Op(command);
        if(op=="M503") foreach(string line in replies) ReadSettings(line);
        if(op=="M114")
            foreach(string line in replies)
            {
                var m=Regex.Match(line,@"^X:\s*([-\d.]+)\s*Y:\s*([-\d.]+)\s*Z:\s*([-\d.]+)");
                if(m.Success) Correct(new(float.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture),float.Parse(m.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture),float.Parse(m.Groups[3].Value,System.Globalization.CultureInfo.InvariantCulture)));
            }
        // These acknowledgements imply the preceding movement has finished.
        if(op is "M400" or "G28" or "G4")
        {
            var end=tail; position=Sample(); steps.Clear(); tail=position; tailTime=clock(); homing=false;
            steps.Enqueue(new(position,end,tailTime,.15,0,.15)); tail=end; tailTime+=.15; SetModalPosition(end);
        }
    }
    public void ReadSettings(string line)
    {
        var m=Regex.Match(line,@"\b(M201|M203|M204|M851)\b.*"); if(!m.Success) return;
        var w=GCode.Words(m.Value); var op=GCode.Op(m.Value);
        if(op is "M201" or "M203")
        {
            var value=op=="M201" ? Profile.MaxAcceleration : Profile.MaxFeed;
            for(int i=0;i<3;i++) if(w.TryGetValue("XYZ"[i],out double v) && v>0) value[i]=(float)v;
            if(op=="M201") Profile.MaxAcceleration=value; else Profile.MaxFeed=value;
            if(op=="M203" && w.TryGetValue('E',out double e) && e>0) Profile.ExtruderFeed=e;
        }
        if(op=="M204")
        {
            if(w.TryGetValue('S',out double a) && a>0) Profile.PrintAcceleration=Profile.TravelAcceleration=a;
            if(w.TryGetValue('P',out a) && a>0) Profile.PrintAcceleration=a;
            if(w.TryGetValue('T',out a) && a>0) Profile.TravelAcceleration=a;
        }
        if(op=="M851")
        {
            var offset=Profile.ProbeOffset;
            for(int i=0;i<3;i++) if(w.TryGetValue("XYZ"[i],out double v)) offset[i]=(float)v;
            Profile.ProbeOffset=offset;
        }
    }
}
