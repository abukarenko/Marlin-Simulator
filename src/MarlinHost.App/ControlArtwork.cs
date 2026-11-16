using System.Drawing.Drawing2D;

namespace MarlinHost;

internal sealed class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private GraphicsPath Outline()
    {
        var path = new GraphicsPath();
        const int diameter = 24;
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(Width-diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(Width-diameter, Height-diameter, diameter, diameter, 0, 90);
        path.AddArc(0, Height-diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Outline();
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 24 || Height < 24) return;
        using var path = Outline();
        var old = Region; Region = new Region(path); old?.Dispose();
    }
}

internal enum ControlSymbol { Nozzle, Bed, Fan, Motion, Extrusion, Speed, Home }

// Vector artwork stays sharp and does not depend on installed symbol fonts.
internal sealed class ControlIcon : Control
{
    private readonly ControlSymbol symbol;
    public ControlIcon(ControlSymbol symbol)
    {
        this.symbol = symbol; BackColor = Color.Black; TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        g.ScaleTransform(Width/48f,Height/48f);
        using var white=new Pen(Color.WhiteSmoke,3) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
        using var red=new Pen(Color.OrangeRed,2) { StartCap=LineCap.Round,EndCap=LineCap.Round };
        switch(symbol)
        {
            case ControlSymbol.Nozzle:
            case ControlSymbol.Extrusion:
                g.FillRectangle(Brushes.Gold,11,5,26,11);
                g.FillRectangle(Brushes.Gold,18,16,12,7);
                g.FillPolygon(Brushes.Gold,[new Point(18,23),new Point(30,23),new Point(24,30)]);
                if(symbol==ControlSymbol.Extrusion) { g.DrawLine(red,24,32,24,44); g.FillEllipse(Brushes.OrangeRed,21,42,6,5); }
                break;
            case ControlSymbol.Bed:
                g.FillPolygon(Brushes.LightGray,[new Point(5,15),new Point(35,15),new Point(43,25),new Point(13,25)]);
                g.DrawLine(white,9,29,40,29);
                for(int x=15;x<=33;x+=9) g.DrawBezier(red,x,44,x-6,38,x+6,37,x,32);
                break;
            case ControlSymbol.Fan:
                g.DrawEllipse(white,4,4,40,40); g.FillEllipse(Brushes.WhiteSmoke,21,21,6,6);
                for(int i=0;i<5;i++)
                {
                    var state=g.Save(); g.TranslateTransform(24,24); g.RotateTransform(i*72);
                    using var blade=new GraphicsPath(); blade.AddBezier(3,-2,22,-22,18,12,5,4); blade.CloseFigure();
                    g.FillPath(Brushes.WhiteSmoke,blade); g.Restore(state);
                }
                break;
            case ControlSymbol.Motion:
                g.DrawLine(white,24,38,24,8); g.DrawLine(white,24,38,44,38); g.DrawLine(white,24,38,7,22);
                g.DrawLines(white,[new Point(18,14),new Point(24,8),new Point(30,14)]);
                g.DrawLines(white,[new Point(38,32),new Point(44,38),new Point(38,44)]);
                break;
            case ControlSymbol.Speed:
                g.FillEllipse(Brushes.WhiteSmoke,28,2,11,11);
                using(var limb=new Pen(Color.WhiteSmoke,6) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round })
                {
                    g.DrawLines(limb,[new Point(30,17),new Point(23,27),new Point(33,32),new Point(42,42)]);
                    g.DrawLines(limb,[new Point(25,29),new Point(17,40),new Point(7,40)]);
                    g.DrawLines(limb,[new Point(28,18),new Point(18,17),new Point(11,25)]);
                    g.DrawLines(limb,[new Point(29,19),new Point(36,25),new Point(43,22)]);
                }
                break;
            case ControlSymbol.Home:
                g.FillPolygon(Brushes.WhiteSmoke,[new Point(3,23),new Point(24,5),new Point(45,23),new Point(39,23),new Point(39,43),new Point(9,43),new Point(9,23)]);
                g.FillRectangle(Brushes.Black,20,28,9,15);
                break;
        }
    }
}
