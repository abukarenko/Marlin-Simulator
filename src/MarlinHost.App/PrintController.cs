using System.Numerics;
using System.Text.RegularExpressions;

namespace MarlinHost;

public enum JobState { Idle, Printing, Pausing, Paused, Resuming, Cancelling, Completed, Cancelled, Faulted }

public sealed class PrintController
{
    private readonly PrinterLink link;
    private readonly SemaphoreSlim operation = new(1);
    private volatile bool pauseRequested;
    private volatile bool cancelRequested;
    private TaskCompletionSource wake = NewSignal();
    private ModalState modal = new();
    public JobState State { get; private set; }
    public bool Active => State is JobState.Printing or JobState.Pausing or JobState.Paused or JobState.Resuming or JobState.Cancelling;
    public int Progress { get; private set; }
    public bool Parked { get; private set; }
    public double? MaxZ { get; private set; }
    public double? MaxX { get; private set; }
    public double? MaxY { get; private set; }
    public double? MinX { get; private set; }
    public double? MinY { get; private set; }
    public Vector3 Position { get; private set; }
    public int Speed { get; private set; } = 100;
    public int Flow { get; private set; } = 100;
    public double NozzleTarget { get; private set; }
    public double BedTarget { get; private set; }
    public event Action? Changed;
    public event Action<string>? Message;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PrintController(PrinterLink link)
    {
        this.link = link;
        link.Lost += _ => { cancelRequested = true; wake.TrySetResult(); if (Active) SetState(JobState.Faulted); };
    }

    private void SetState(JobState state) { State = state; Changed?.Invoke(); }
    private async Task<string[]> Send(string command, bool track = true)
    {
        var replies = await link.Send(command);
        if (track) modal.Apply(command);
        var words = GCode.Words(command);
        switch (GCode.Op(command))
        {
            case "M220": if (words.TryGetValue('S', out var speed)) Speed = (int)speed; break;
            case "M221": if (words.TryGetValue('S', out var flow)) Flow = (int)flow; break;
            case "M104": case "M109": if (words.TryGetValue('S', out var nozzle) || words.TryGetValue('R', out nozzle)) NozzleTarget = nozzle; break;
            case "M140": case "M190": if (words.TryGetValue('S', out var bed) || words.TryGetValue('R', out bed)) BedTarget = bed; break;
        }
        Changed?.Invoke();
        return replies;
    }

    public async Task Initialize()
    {
        await operation.WaitAsync();
        try
        {
            await ReadBounds();
            await Send("M105");
            await Send("M114");
            await Send("M119");
        }
        finally { operation.Release(); }
    }

    private async Task ReadBounds()
    {
        MinX = MinY = MaxX = MaxY = MaxZ = null;
        foreach (string line in await Send("M211"))
        {
            double V(Group g) => double.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture);
            var min = Regex.Match(line, @"Min:\s*X:?\s*([-\d.]+)\s*Y:?\s*([-\d.]+)");
            if (min.Success) { MinX = V(min.Groups[1]); MinY = V(min.Groups[2]); }
            var max = Regex.Match(line, @"Max:\s*X:?\s*([-\d.]+)\s*Y:?\s*([-\d.]+)\s*Z:?\s*([-\d.]+)");
            if (max.Success) { MaxX = V(max.Groups[1]); MaxY = V(max.Groups[2]); MaxZ = V(max.Groups[3]); }
        }
    }

    public async Task<string[]> Manual(string script, bool live = false)
    {
        await operation.WaitAsync();
        try
        {
            if (Active && !live) throw new InvalidOperationException("Ручные команды и скрипты доступны вне задания.");
            var commands = script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(GCode.Clean).Where(c => c.Length > 0).ToArray();
            if (live && commands.Any(c => GCode.Op(c) is not ("M105" or "M114" or "M119" or "M104" or "M140" or "M106" or "M107" or "M220" or "M221")))
                throw new InvalidOperationException("Эта команда не разрешена во время печати.");
            var result = new List<string>();
            foreach (string command in commands) result.AddRange(await Send(command));
            return result.ToArray();
        }
        finally { operation.Release(); }
    }

    public async Task Jog(char axis, double distance, double feed)
    {
        await operation.WaitAsync();
        try
        {
            if (Active) throw new InvalidOperationException("Перемещения доступны вне задания.");
            var saved = modal.Copy();
            await Send("G21", false); await Send("G91", false);
            await Send($"G1 {axis}{GCode.N(distance)} F{GCode.N(feed)}", false);
            foreach (string command in saved.RestoreModes()) await Send(command, false);
        }
        finally { operation.Release(); }
    }

    public async Task Extrude(double length, double feed)
    {
        await operation.WaitAsync();
        try
        {
            if (Active) throw new InvalidOperationException("Экструзия доступна вне задания.");
            var saved = modal.Copy();
            await Send("G21", false); await Send("M83", false);
            await Send($"G1 E{GCode.N(length)} F{GCode.N(feed)}", false);
            await Send("G92 E" + GCode.N(saved.E), false);
            foreach (string command in saved.RestoreModes()) await Send(command, false);
        }
        finally { operation.Release(); }
    }

    public void Pause()
    {
        if (State != JobState.Printing) return;
        pauseRequested = true; SetState(JobState.Pausing);
        Message?.Invoke("Пауза запрошена. Ожидаем текущую команду и завершение движений.");
    }
    public void Resume()
    {
        if (State != JobState.Paused) return;
        SetState(JobState.Resuming); wake.TrySetResult();
    }
    public void Cancel()
    {
        if (!Active) return;
        cancelRequested = true; SetState(JobState.Cancelling); wake.TrySetResult();
        Message?.Invoke("Отмена запрошена. Ожидаем текущую команду; затем выключим нагрев и вентилятор.");
    }

    private static (Vector3 Position, double E) ParsePosition(string[] lines)
    {
        var match = lines.Select(l => Regex.Match(l, @"X:\s*([-\d.]+)\s*Y:\s*([-\d.]+)\s*Z:\s*([-\d.]+)\s*E:\s*([-\d.]+)")).FirstOrDefault(m => m.Success)
            ?? throw new IOException("Прошивка не сообщила положение. Парковка отменена.");
        double V(int i) => double.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (new((float)V(1), (float)V(2), (float)V(3)), V(4));
    }

    private async Task ParkAndWait()
    {
        ModalState saved;
        Vector3 at = default;
        double e = 0, nozzle, bed;
        Parked = false;
        wake = NewSignal();
        await operation.WaitAsync();
        try
        {
            await Send("M400");
            saved = modal.Copy(); nozzle = NozzleTarget; bed = BedTarget;
            await ReadBounds();
            if (!saved.Homed || saved.Inches || MaxZ is null || MinX is null || MinY is null || MinX > 0 || MinY > 0 || MaxX is null or < 0 || MaxY is null or < 0)
                Message?.Invoke("Пауза без парковки: ещё не выполнен G28 всех осей либо неизвестны границы. Неподтверждённые перемещения не выполняются.");
            else
            {
                (at, e) = ParsePosition(await Send("M114"));
                if (at.Z > MaxZ || at.Z < 0) throw new IOException("Z вне известных границ. Парковка запрещена.");
                double lift = Math.Min(at.Z + 15, MaxZ.Value);
                await Send("G21", false); await Send("G90", false); await Send("M82", false);
                await Send($"G1 Z{GCode.N(lift)} F600", false);
                await Send("G1 X0 Y0 F3000", false);
                await Send("M400", false);
                Parked = true;
            }
            SetState(cancelRequested ? JobState.Cancelling : JobState.Paused);
        }
        finally { operation.Release(); }
        if (!cancelRequested) await wake.Task;
        if (cancelRequested) return;
        await operation.WaitAsync();
        try
        {
            if (bed > 0) await Send("M190 R" + GCode.N(bed), false);
            if (cancelRequested) return;
            if (nozzle > 0) await Send("M109 R" + GCode.N(nozzle), false);
            if (cancelRequested) return;
            if (Parked)
            {
                await Send("G21", false); await Send("G90", false); await Send("M82", false);
                await Send($"G1 X{GCode.N(at.X)} Y{GCode.N(at.Y)} F3000", false);
                if (cancelRequested) return;
                await Send("G1 Z" + GCode.N(at.Z) + " F600", false);
                await Send("G92 E" + GCode.N(e), false);
                foreach (string command in saved.RestoreModes()) await Send(command, false);
                await Send("M400", false);
            }
            modal = saved; NozzleTarget = nozzle; BedTarget = bed;
            Parked = false; pauseRequested = false; SetState(JobState.Printing);
        }
        finally { operation.Release(); }
    }

    public async Task Run(PrintJob job)
    {
        if (Active) throw new InvalidOperationException("Задание уже выполняется.");
        pauseRequested = cancelRequested = Parked = false; Progress = 0;
        SetState(JobState.Printing);
        try
        {
            for (int i = 0; i < job.Commands.Length; i++)
            {
                if (cancelRequested) break;
                if (pauseRequested) await ParkAndWait();
                if (cancelRequested) break;
                if (GCode.Pause(job.Commands[i])) { Message?.Invoke("Пауза из файла: " + job.Commands[i]); pauseRequested = true; SetState(JobState.Pausing); await ParkAndWait(); }
                else
                {
                    await operation.WaitAsync();
                    try { if (!cancelRequested) await Send(job.Commands[i]); }
                    finally { operation.Release(); }
                }
                Progress = i + 1; Changed?.Invoke();
                // Let queued controls and telemetry run between file commands.
                await Task.Yield();
            }
            await operation.WaitAsync();
            try
            {
                await Send("M400");
                if (cancelRequested)
                {
                    foreach (string c in new[] { "M104 S0", "M140 S0", "M107" }) await Send(c);
                    SetState(JobState.Cancelled);
                }
                else SetState(JobState.Completed);
            }
            finally { operation.Release(); }
        }
        catch { SetState(JobState.Faulted); throw; }
    }

    public async Task Emergency()
    {
        cancelRequested = true; wake.TrySetResult();
        await link.Raw("M112");
        SetState(JobState.Faulted);
        await link.DisposeAsync();
    }
}
