using System.Drawing.Drawing2D;
using System.Globalization;

namespace MarlinHost;

// The upper pointer is editable; the lower marker always comes from telemetry.
public sealed class TemperatureControl : Control
{
    private readonly NumericUpDown target;
    private readonly CheckBox heating;
    private double? actual;
    private bool dragging;
    public event EventHandler? SetpointCommitted;
    public double? ActualTemperature => actual;

    public TemperatureControl(string title, NumericUpDown target, CheckBox heating)
    {
        this.target = target; this.heating = heating;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Text = title; Size = new Size(298, 90); BackColor = Color.Transparent; ForeColor = Color.WhiteSmoke;
        TabStop = true; AccessibleName = title + ": установка температуры";
        target.SetBounds(185, 4, 66, 25); target.ForeColor = Color.Black;
        heating.SetBounds(269, 7, 19, 19); heating.AccessibleName = title + ": нагрев";
        Controls.AddRange([target, heating]);
        target.ValueChanged += (_, _) => Invalidate();
        target.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetpointCommitted?.Invoke(this, EventArgs.Empty); } };
        target.Validated += (_, _) => SetpointCommitted?.Invoke(this, EventArgs.Empty);
    }

    public void SetActual(double? value) { actual = value; Invalidate(); }
    public void SetReportedTemperature(double value, double requested)
    {
        // Follow changes from the print/standby commands, without erasing the
        // remembered setpoint when heating is switched off or interrupting edits.
        if (requested > 0 && !ContainsFocus && !dragging)
            target.Value = Math.Clamp((decimal)requested, target.Minimum, target.Maximum);
        SetActual(value);
    }
    private float Position(double value) => 9 + (float)Math.Clamp(value / (double)target.Maximum, 0, 1) * 156;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var ink = new SolidBrush(Enabled ? ForeColor : Color.Gray);
        g.DrawString(Text + ", °C", Font, ink, 7, 7);
        g.DrawString("Установка", Font, ink, 115, 7);
        using var rail = new SolidBrush(Color.LightGray); g.FillRectangle(rail, 9, 40, 156, 4);
        float x = Position((double)target.Value);
        using var blue = new SolidBrush(Enabled ? Color.FromArgb(0, 150, 235) : Color.Gray);
        g.FillPolygon(blue, [new PointF(x-7,32),new PointF(x+7,32),new PointF(x+7,46),new PointF(x,53),new PointF(x-7,46)]);
        var bar = new Rectangle(9, 65, 156, 10);
        using var gradient = new LinearGradientBrush(bar, Color.LimeGreen, Color.Red, 0f);
        gradient.InterpolationColors = new ColorBlend { Colors = [Color.FromArgb(15,185,75),Color.YellowGreen,Color.Yellow,Color.Orange,Color.Red], Positions = [0f,.3f,.5f,.72f,1f] };
        g.FillRectangle(gradient, bar);
        if (actual is double measured)
        {
            float marker = Position(measured);
            g.FillEllipse(Brushes.White, marker-4, 65, 8, 9);
            using var edge = new Pen(Color.RoyalBlue); g.DrawEllipse(edge, marker-4, 65, 8, 9);
        }
        string reading = actual is double temperature ? temperature.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " °C" : "— °C";
        g.DrawString("Датчик " + reading, Font, ink, 175, 61);
        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(5, 29, 165, 27));
    }
    private void MovePointer(int x) => target.Value = Math.Clamp((decimal)Math.Round((x-9) / 156.0 * (double)target.Maximum), target.Minimum, target.Maximum);
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y < 29 || e.Y > 56) return;
        Focus(); dragging = true; Capture = true; MovePointer(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) MovePointer(e.X); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (!dragging) return;
        MovePointer(e.X); dragging = false; Capture = false; SetpointCommitted?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int delta = e.KeyCode is Keys.Right or Keys.Up ? 1 : e.KeyCode is Keys.Left or Keys.Down ? -1 : 0;
        if (delta == 0) return;
        target.Value = Math.Clamp(target.Value + delta, target.Minimum, target.Maximum);
        e.Handled = true; SetpointCommitted?.Invoke(this, EventArgs.Empty);
    }
}
