using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace MarlinHost;

public sealed class MotionLimits
{
    public double[] Steps { get; } = [80,80,400,95];
    public double[] Speed { get; } = [300,300,10,25];
    public double[] Acceleration { get; } = [1000,1000,100,1000];
    public double PrintAcceleration { get; private set; } = 500;
    public double TravelAcceleration { get; private set; } = 500;
    public double RetractAcceleration { get; private set; } = 1000;
    public double MinimumPrintSpeed { get; private set; }
    public double MinimumTravelSpeed { get; private set; }
    public bool HasSteps { get; private set; }
    public bool HasAcceleration { get; private set; }

    public void Apply(string line)
    {
        var setting=Regex.Match(line.ToUpperInvariant(), @"\bM(?:92|201|203|204|205)\b.*");
        if(!setting.Success) return;
        var words=GCode.Words(setting.Value);
        void Axes(double[] values)
        {
            for(int i=0;i<4;i++) if(words.TryGetValue("XYZE"[i],out double value) && value>0) values[i]=value;
        }
        switch(GCode.Op(setting.Value))
        {
            case "M92": Axes(Steps); HasSteps=words.ContainsKey('X') && words.ContainsKey('Y') && words.ContainsKey('Z') || HasSteps; break;
            case "M201": Axes(Acceleration); HasAcceleration=true; break;
            case "M203": Axes(Speed); break;
            case "M204":
                if(words.TryGetValue('S',out double legacy) && legacy>0) PrintAcceleration=TravelAcceleration=legacy;
                if(words.TryGetValue('P',out double print) && print>0) PrintAcceleration=print;
                if(words.TryGetValue('T',out double travel) && travel>0) TravelAcceleration=travel;
                if(words.TryGetValue('R',out double retract) && retract>0) RetractAcceleration=retract;
                break;
            case "M205":
                if(words.TryGetValue('S',out double minPrint) && minPrint>=0) MinimumPrintSpeed=minPrint;
                if(words.TryGetValue('T',out double minTravel) && minTravel>=0) MinimumTravelSpeed=minTravel;
                break;
        }
    }
}

// Fallback for firmware without M114_REALTIME. Isolated moves use a
// triangular/trapezoidal profile; Marlin's full lookahead is not duplicated.
public sealed class MotionProfile
{
    private readonly IReadOnlyList<Vector3> path;
    public Vector3 From { get; }
    public Vector3 To { get; }
    public double Start { get; }
    public double Length { get; }
    public double Acceleration { get; }
    public double PeakSpeed { get; }
    public double RampTime { get; }
    public double CruiseTime { get; }
    public double Duration => 2*RampTime+CruiseTime;
    public double End => Start+Duration;

    public MotionProfile(Vector3 from,Vector3 to,double extrusion,double feed,int percent,MotionLimits limits,double start,IReadOnlyList<Vector3>? path=null)
    {
        From=from; To=to; Start=start;
        this.path=path ?? [to];
        double[] ratios=[0,0,0,0];
        var previous=from;
        foreach(var point in this.path)
        {
            var d=point-previous; double length=d.Length(); Length+=length;
            if(length>1e-8)
            {
                ratios[0]=Math.Max(ratios[0],Math.Abs(d.X)/length);
                ratios[1]=Math.Max(ratios[1],Math.Abs(d.Y)/length);
                ratios[2]=Math.Max(ratios[2],Math.Abs(d.Z)/length);
            }
            previous=point;
        }
        bool retract=Length<1e-8;
        if(retract) Length=Math.Abs(extrusion);
        if(Length<1e-8) return;
        ratios[3]=Math.Abs(extrusion)/Length;
        double speed=Math.Max(feed/60*Math.Max(1,percent)/100.0,extrusion>0 ? limits.MinimumPrintSpeed : limits.MinimumTravelSpeed);
        double acceleration=retract ? limits.RetractAcceleration : extrusion>0 ? limits.PrintAcceleration : limits.TravelAcceleration;
        for(int i=0;i<4;i++) if(ratios[i]>1e-8)
        {
            speed=Math.Min(speed,limits.Speed[i]/ratios[i]);
            acceleration=Math.Min(acceleration,limits.Acceleration[i]/ratios[i]);
        }
        Acceleration=Math.Max(.001,acceleration);
        PeakSpeed=Math.Min(Math.Max(.000001,speed),Math.Sqrt(Length*Acceleration));
        RampTime=PeakSpeed/Acceleration;
        CruiseTime=Math.Max(0,(Length-PeakSpeed*RampTime)/PeakSpeed);
    }
    public Vector3 At(double now)
    {
        if(Length<1e-8 || now>=End) return To;
        double t=Math.Max(0,now-Start), distance;
        if(t<RampTime) distance=.5*Acceleration*t*t;
        else if(t<RampTime+CruiseTime) distance=.5*PeakSpeed*RampTime+PeakSpeed*(t-RampTime);
        else { double left=Duration-t; distance=Length-.5*Acceleration*left*left; }
        var previous=From;
        foreach(var point in path)
        {
            double length=Vector3.Distance(previous,point);
            if(distance<=length && length>1e-8) return Vector3.Lerp(previous,point,(float)Math.Clamp(distance/length,0,1));
            distance-=length; previous=point;
        }
        return To;
    }
}

public sealed class MotionPreview
{
    private readonly object sync=new();
    private readonly Func<double> clock;
    private readonly Queue<MotionProfile> moves=[];
    private readonly ModalState planned=new();
    private Vector3 position;
    private Vector3? measured;
    private double available;
    private int percent=100;
    private int plane=17;
    private bool seeded;
    public MotionLimits Limits { get; } = new();
    public bool RealtimeObserved { get; private set; }
    public MotionPreview(Func<double>? clock=null) { this.clock=clock ?? (()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency); }

    public Vector3 Position { get { lock(sync) return Sample(clock()); } }
    private Vector3 Sample(double now)
    {
        while(moves.TryPeek(out var move) && now>=move.End) { position=move.To; moves.Dequeue(); }
        if(RealtimeObserved && measured.HasValue) return measured.Value;
        return moves.TryPeek(out var first) ? first.At(now) : position;
    }
    public void Stop() { lock(sync) { position=Sample(clock()); measured=position; moves.Clear(); available=clock(); planned.SetPosition(position); } }
    public void Settings(IEnumerable<string> lines) { lock(sync) foreach(string line in lines) Limits.Apply(line); }

    public void Observe(IEnumerable<string> replies,bool realtimeRequested,bool synchronized=false)
    {
        var match=replies.Select(l=>Regex.Match(l,@"^X:\s*([-\d.]+)\s*Y:\s*([-\d.]+)\s*Z:\s*([-\d.]+)(?:\s*E:\s*([-\d.]+))?")).FirstOrDefault(m=>m.Success);
        if(match is null) return;
        double V(int i)=>double.Parse(match.Groups[i].Value,CultureInfo.InvariantCulture);
        var report=new Vector3((float)V(1),(float)V(2),(float)V(3));
        lock(sync)
        {
            double now=clock(); Sample(now);
            // Unsupported M114 R silently becomes ordinary M114. Its projected
            // endpoint must not short-circuit a pending slow move.
            if(realtimeRequested && moves.Count>0 && Vector3.Distance(report,planned.Position)>.03f)
            {
                bool intermediate=moves.Any(move=>
                {
                    var delta=move.To-move.From; double length2=delta.LengthSquared();
                    if(length2<1e-8) return false;
                    double fraction=Vector3.Dot(report-move.From,delta)/length2;
                    return fraction>0 && fraction<1 && Vector3.Distance(report,Vector3.Lerp(move.From,move.To,(float)fraction))<.1;
                });
                if(intermediate) RealtimeObserved=true;
            }
            if(RealtimeObserved && realtimeRequested) measured=report;
            if(!seeded || synchronized || moves.Count==0 && !RealtimeObserved)
            {
                position=report; measured=report; planned.SetPosition(report,match.Groups[4].Success ? V(4) : null);
                moves.Clear(); available=now; seeded=true;
            }
        }
    }

    public void Accept(string command,IEnumerable<string>? replies=null)
    {
        lock(sync)
        {
            double now=clock(); Sample(now);
            string op=GCode.Op(command); var words=GCode.Words(command);
            Limits.Apply(command);
            if(op=="M220" && words.TryGetValue('S',out double scale)) percent=(int)scale;
            if(op is "G17" or "G18" or "G19") plane=int.Parse(op[1..]);
            var from=planned.Position; double e=planned.E; planned.Apply(command);
            if(op is "G0" or "G00" or "G1" or "G01" or "G2" or "G3")
            {
                var end=planned.Position;
                var points=op is "G2" or "G3" ? PrintJob.Arc(from,end,command,plane,planned.Inches ? 25.4 : 1) : new List<Vector3>();
                if(points.Count==0) points.Add(end);
                var move=new MotionProfile(from,end,planned.E-e,planned.Feed,percent,Limits,Math.Max(now,available),points);
                if(move.Duration>0) { moves.Enqueue(move); available=move.End; }
            }
            else if(op is "M400" or "G28" or "G4" || op is "M18" or "M84" && !words.ContainsKey('S'))
            {
                position=planned.Position; measured=position; moves.Clear(); available=now;
                if(replies is not null && op=="G28") Observe(replies,false,true);
            }
            else if(op=="G92" && words.Keys.Any(axis=>axis is 'X' or 'Y' or 'Z')) { position=planned.Position; measured=position; moves.Clear(); available=now; }
            else if(op is "M410" or "M112") Stop();
            if(op=="M114" && replies is not null) Observe(replies,command.Contains('R'));
        }
    }
}
