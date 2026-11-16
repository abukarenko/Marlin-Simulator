using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace MarlinHost;

public static class GCode
{
    public static string Clean(string line) => Regex.Replace(line.Split(';')[0], @"\([^)]*\)", "").Trim().ToUpperInvariant();
    public static string Op(string line) => Regex.Match(line, @"^[GMT]\d+(?:\.\d+)?").Value;
    public static Dictionary<char, double> Words(string line) => Regex.Matches(line, @"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))")
        .Cast<Match>().GroupBy(m => m.Groups[1].Value[0]).ToDictionary(g => g.Key, g => double.Parse(g.Last().Groups[2].Value, CultureInfo.InvariantCulture));
    public static string N(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    public static bool Pause(string line) => Op(line) is "M0" or "M1" or "M25" or "M600" or "M601";
}

public sealed class ModalState
{
    public bool Relative { get; private set; }
    public bool ERelative { get; private set; }
    public bool Inches { get; private set; }
    public bool Homed { get; private set; }
    private int homedAxes;
    public double Feed { get; private set; } = 1200;
    public Vector3 Position { get; private set; }
    public double E { get; private set; }
    public ModalState Copy() => (ModalState)MemberwiseClone();
    public void Apply(string line)
    {
        var w = GCode.Words(line); string op = GCode.Op(line);
        double unit = Inches ? 25.4 : 1;
        switch (op)
        {
            case "G20": Inches = true; break;
            case "G21": Inches = false; break;
            case "G90": Relative = false; ERelative = false; break;
            case "G91": Relative = true; ERelative = true; break;
            case "M82": ERelative = false; break;
            case "M83": ERelative = true; break;
            case "G28":
                // Only an all-axis home establishes a position safe for automatic parking.
                if (!line[3..].Any(c => c is 'X' or 'Y' or 'Z')) { homedAxes=7; Position=Vector3.Zero; }
                else { if(line[3..].Contains('X')) homedAxes|=1; if(line[3..].Contains('Y')) homedAxes|=2; if(line[3..].Contains('Z')) homedAxes|=4; }
                Homed=homedAxes==7;
                break;
            case "M18": case "M84": case "M410": Homed = false; homedAxes=0; break;
            case "G92":
                Position = new((float)(w.GetValueOrDefault('X', Position.X / unit) * unit), (float)(w.GetValueOrDefault('Y', Position.Y / unit) * unit), (float)(w.GetValueOrDefault('Z', Position.Z / unit) * unit));
                if (w.TryGetValue('E', out double e0)) E = e0 * unit;
                break;
            case "G0": case "G00": case "G1": case "G01": case "G2": case "G3":
                float Axis(char axis, float previous) => w.TryGetValue(axis, out double value) ? (float)(value * unit + (Relative ? previous : 0)) : previous;
                Position = new(Axis('X', Position.X), Axis('Y', Position.Y), Axis('Z', Position.Z));
                if (w.TryGetValue('E', out double e)) E = e * unit + (ERelative ? E : 0);
                if (w.TryGetValue('F', out double f)) Feed = f * unit;
                break;
        }
    }
    public IEnumerable<string> RestoreModes()
    {
        yield return Inches ? "G20" : "G21";
        yield return Relative ? "G91" : "G90";
        yield return ERelative ? "M83" : "M82";
        yield return "G1 F" + GCode.N(Feed / (Inches ? 25.4 : 1));
    }
}

public sealed record PathSegment(Vector3 From, Vector3 To, bool Extruding, int Command, bool Outline=true);
public sealed record PrintJob(string FileName, string[] Commands, List<PathSegment> Segments, bool HasArcs)
{
    public Color FilamentColor { get; init; } = Color.FromArgb(255,128,0);
    public int[] LayerEndCommands { get; init; } = [];
    public bool EstimatedLayers { get; init; }
    public ProbeRectangle? ModelFootprint { get; init; }
    public int FirstExtrusionCommand { get; init; } = -1;
    public int PrintedLayers(int confirmedCommands)
    {
        int index = Array.BinarySearch(LayerEndCommands, confirmedCommands - 1);
        return index >= 0 ? index + 1 : ~index;
    }
    public static PrintJob Load(string path)
    {
        if (new FileInfo(path).Length > 150_000_000) throw new IOException("Для первой версии размер задания ограничен 150 МБ.");
        Color filament=Color.FromArgb(255,128,0);
        var commands=new List<string>();
        var modelCommands=new HashSet<int>(); string currentType="";
        var layerStarts = new List<int>();
        var outlineCommands=new HashSet<int>(); bool outer=false;
        foreach(string raw in File.ReadLines(path))
        {
            string comment = raw.Trim();
            if(comment.StartsWith(";TYPE:",StringComparison.OrdinalIgnoreCase)) currentType=comment[6..].Trim();
            if (comment.Equals(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(comment, @"^;LAYER:\s*\d+\s*$", RegexOptions.IgnoreCase))
                if (layerStarts.Count == 0 || layerStarts[^1] != commands.Count) layerStarts.Add(commands.Count);
            if(raw.StartsWith(";TYPE:",StringComparison.OrdinalIgnoreCase))
                outer=raw.Contains("External perimeter",StringComparison.OrdinalIgnoreCase) || raw.Contains("WALL-OUTER",StringComparison.OrdinalIgnoreCase) || raw.Contains("Outer wall",StringComparison.OrdinalIgnoreCase);
            if(raw.StartsWith("; filament_colour",StringComparison.OrdinalIgnoreCase))
            {
                var color=Regex.Match(raw,@"#[0-9a-fA-F]{6}");
                if(color.Success) filament=ColorTranslator.FromHtml(color.Value);
            }
            string clean=GCode.Clean(raw); if(clean.Length>0) {
                if(outer) outlineCommands.Add(commands.Count);
                if(!currentType.Equals("Custom",StringComparison.OrdinalIgnoreCase) &&
                   !currentType.Contains("Skirt",StringComparison.OrdinalIgnoreCase) &&
                   !currentType.Contains("Brim",StringComparison.OrdinalIgnoreCase) &&
                   !currentType.Contains("Wipe tower",StringComparison.OrdinalIgnoreCase)) modelCommands.Add(commands.Count);
                commands.Add(clean);
            }
        }
        var lines=commands.ToArray();
        if (lines.Length == 0) throw new IOException("В файле нет команд.");
        if (lines.Any(l => l.StartsWith('N') || l.Contains('*') || l.Any(c => c > 127) || l.IndexOf('\0') >= 0))
            throw new IOException("Нужен обычный текстовый G-code без номеров строк и контрольных сумм.");
        if (lines.Any(l => Regex.IsMatch(l, @"^T[1-9]"))) throw new IOException("В версии 0.1 доступен только экструдер T0.");
        var state = new ModalState(); var segments = new List<PathSegment>(); bool arcs = false; int plane = 17, firstExtrusion=-1;
        for (int i = 0; i < lines.Length; i++)
        {
            string op = GCode.Op(lines[i]);
            if (op is "G17" or "G18" or "G19") plane = int.Parse(op[1..]);
            var from = state.Position; double e = state.E; double unit = state.Inches ? 25.4 : 1; state.Apply(lines[i]);
            if (GCode.Op(lines[i]) is "G0" or "G00" or "G1" or "G01" or "G2" or "G3")
            {
                if(state.E>e && firstExtrusion<0) firstExtrusion=i;
                if (op is "G2" or "G3")
                {
                    var points = Arc(from, state.Position, lines[i], plane, unit);
                    if (points.Count == 0) { arcs = true; if (from != state.Position) segments.Add(new(from, state.Position, state.E > e, i,outlineCommands.Count==0 || outlineCommands.Contains(i))); }
                    else foreach (var point in points) { segments.Add(new(from, point, state.E > e, i,outlineCommands.Count==0 || outlineCommands.Contains(i))); from = point; }
                }
                else if (from != state.Position) segments.Add(new(from, state.Position, state.E > e, i,outlineCommands.Count==0 || outlineCommands.Contains(i)));
            }
        }
        var layerEnds = new List<int>();
        bool estimated = layerStarts.Count == 0;
        if (!estimated)
        {
            int layer = 0, last = -1;
            foreach (var segment in segments)
            {
                while (layer + 1 < layerStarts.Count && segment.Command >= layerStarts[layer + 1])
                { if (last >= 0) layerEnds.Add(last); last = -1; layer++; }
                if (segment.Command >= layerStarts[layer] && segment.Extruding) last = segment.Command;
            }
            if (last >= 0) layerEnds.Add(last);
        }
        else
        {
            float height = float.NegativeInfinity;
            foreach (var segment in segments.Where(s => s.Extruding))
            {
                if (layerEnds.Count == 0 || segment.To.Z > height + .001f)
                { layerEnds.Add(segment.Command); height = segment.To.Z; }
                else layerEnds[^1] = segment.Command;
            }
        }
        var deposited=segments.Where(s=>s.Extruding && modelCommands.Contains(s.Command)).ToArray();
        ProbeRectangle? footprint=deposited.Length==0 ? null : new(
            deposited.Min(s=>Math.Min(s.From.X,s.To.X)),deposited.Max(s=>Math.Max(s.From.X,s.To.X)),
            deposited.Min(s=>Math.Min(s.From.Y,s.To.Y)),deposited.Max(s=>Math.Max(s.From.Y,s.To.Y)));
        return new(path, lines, segments, arcs) { FilamentColor=filament, LayerEndCommands=layerEnds.ToArray(), EstimatedLayers=estimated,
            ModelFootprint=footprint, FirstExtrusionCommand=firstExtrusion };
    }

    internal static List<Vector3> Arc(Vector3 start, Vector3 end, string command, int plane, double unit)
    {
        Vector3 Project(Vector3 p) => plane switch { 18 => new(p.Z, p.X, p.Y), 19 => new(p.Y, p.Z, p.X), _ => p };
        Vector3 Unproject(Vector3 p) => plane switch { 18 => new(p.Y, p.Z, p.X), 19 => new(p.Z, p.X, p.Y), _ => p };
        var a = Project(start); var b = Project(end); var words = GCode.Words(command);
        char first = plane == 18 ? 'K' : plane == 19 ? 'J' : 'I';
        char second = plane == 18 ? 'I' : plane == 19 ? 'K' : 'J';
        bool clockwise = GCode.Op(command) == "G2";
        double Sweep(double cx, double cy)
        {
            double result = Math.Atan2(b.Y - cy, b.X - cx) - Math.Atan2(a.Y - cy, a.X - cx);
            if (clockwise) { while (result >= -1e-8) result -= Math.Tau; }
            else { while (result <= 1e-8) result += Math.Tau; }
            return result;
        }
        double cx, cy;
        if (words.TryGetValue('R', out double r))
        {
            r *= unit; double dx = b.X - a.X, dy = b.Y - a.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-8 || d > Math.Abs(2 * r)) return [];
            double h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
            cx = (a.X + b.X) / 2 - dy / d * h; cy = (a.Y + b.Y) / 2 + dx / d * h;
            bool major = Math.Abs(Sweep(cx, cy)) > Math.PI + 1e-6;
            if (major != (r < 0)) { cx = (a.X + b.X) / 2 + dy / d * h; cy = (a.Y + b.Y) / 2 - dx / d * h; }
        }
        else if (words.ContainsKey(first) || words.ContainsKey(second))
        { cx = a.X + words.GetValueOrDefault(first) * unit; cy = a.Y + words.GetValueOrDefault(second) * unit; }
        else return [];
        double radius = Math.Sqrt(Math.Pow(a.X - cx, 2) + Math.Pow(a.Y - cy, 2));
        double endRadius = Math.Sqrt(Math.Pow(b.X - cx, 2) + Math.Pow(b.Y - cy, 2));
        if (radius < 1e-8 || Math.Abs(radius - endRadius) > .1) return [];
        double angle = Math.Atan2(a.Y - cy, a.X - cx), sweep = Sweep(cx, cy);
        int steps = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(sweep) * 40, Math.Abs(sweep) * radius)), 2, 4096);
        var points = new List<Vector3>(steps);
        for (int i = 1; i < steps; i++)
        {
            double t = (double)i / steps;
            points.Add(Unproject(new((float)(cx + radius * Math.Cos(angle + sweep * t)), (float)(cy + radius * Math.Sin(angle + sweep * t)), (float)(a.Z + (b.Z - a.Z) * t))));
        }
        points.Add(end); return points;
    }
}
