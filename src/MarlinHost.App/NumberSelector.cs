using System.ComponentModel;
using System.Globalization;

namespace MarlinHost;

internal sealed class NumberSelector : ComboBox
{
    private readonly double maximum;
    private readonly string caption;

    public NumberSelector(string caption, string[] choices, string initial, double maximum)
    {
        this.caption=caption; this.maximum=maximum;
        DropDownStyle=ComboBoxStyle.DropDown;
        Items.AddRange(choices); Text=initial;
        AccessibleName=caption;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get
        {
            if(!double.TryParse(Text.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double value)
                || !double.IsFinite(value) || value<=0 || value>maximum)
                throw new ArgumentException($"{caption}: введите число больше 0 и не больше {maximum}.");
            return value;
        }
    }
}
