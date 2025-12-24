using System.Windows.Media;

namespace WpfXrayQA.Models
{
    public class OverlayShape
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Diameter { get; set; }
        public bool IsFoundByBlob { get; set; }
        public string TooltipInfo { get; set; } = string.Empty;

        public string State { get; set; } = "OK"; // "OK", "NG", "EXTRA"

        // Tọa độ vẽ trên Canvas
        public double Left => X - (Diameter / 2);
        public double Top => Y - (Diameter / 2);

        public PointCollection ContourPoints { get; set; }

        public double StrokeThickness => State == "OK" ? 1 : 2;

        // [FIX] THÊM THUỘC TÍNH NÀY ĐỂ UI HIỂN THỊ ĐƯỢC MÀU
        public SolidColorBrush StrokeBrush
        {
            get
            {
                switch (State)
                {
                    case "NG": return Brushes.Red;
                    case "EXTRA": return Brushes.Yellow;
                    default: return Brushes.Lime; // OK là màu Lime
                }
            }
        }
    }
}