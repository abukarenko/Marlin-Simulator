using System.Globalization;

namespace MarlinHost;

public sealed record ProbeRectangle(double Left, double Right, double Front, double Back)
{
    public string Arguments => $"L{GCode.N(Left)} R{GCode.N(Right)} F{GCode.N(Front)} B{GCode.N(Back)}";
    public override string ToString() => $"X {GCode.N(Left)}…{GCode.N(Right)}, Y {GCode.N(Front)}…{GCode.N(Back)}";
}

public static class FourPointLeveling
{
    public static ProbeRectangle ReadLimits(IEnumerable<string> replies)
    {
        string line=replies.FirstOrDefault(l=>l.StartsWith("FOUR_POINT_CAPS V1 ",StringComparison.Ordinal))
            ?? throw new IOException("Прошивка не сообщила границы четырёхточечного замера.");
        var w=GCode.Words(line);
        if(!new[]{'L','R','F','B'}.All(k=>w.ContainsKey(k) && double.IsFinite(w[k])) || w['R']<=w['L'] || w['B']<=w['F'])
            throw new IOException("Некорректные границы датчика в ответе прошивки.");
        return new(w['L'],w['R'],w['F'],w['B']);
    }
    public static ProbeRectangle Plan(PrintJob job,ProbeRectangle limits)
    {
        var model=job.ModelFootprint ?? throw new IOException("Нет траекторий модели для определения области выравнивания.");
        if(job.HasArcs) throw new IOException("Не удалось разобрать дуги модели: область выравнивания неизвестна.");
        if(model.Left<limits.Left || model.Right>limits.Right || model.Front<limits.Front || model.Back>limits.Back)
            throw new IOException($"Область модели ({model}) выходит за доступную область датчика ({limits}). Переместите модель на столе.");
        // The 2 mm margin also covers arc tessellation rounding. Never move beyond probe limits.
        var area=new ProbeRectangle(Math.Max(limits.Left,model.Left-2),Math.Min(limits.Right,model.Right+2),
            Math.Max(limits.Front,model.Front-2),Math.Min(limits.Back,model.Back+2));
        if(area.Right-area.Left<1 || area.Back-area.Front<1) throw new IOException("Область модели слишком мала для четырёхточечного замера.");
        return area;
    }
}
