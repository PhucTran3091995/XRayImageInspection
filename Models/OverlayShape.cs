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

        // [MỚI] Các thuộc tính hỗ trợ vẽ đường Short (Line)
        public double RelativeX2 => X2 - X;
        public double RelativeY2 => Y2 - Y;
        public bool IsLine { get; set; } = false; // Đánh dấu đây là đường nối
        public double X2 { get; set; }            // Điểm cuối X
        public double Y2 { get; set; }            // Điểm cuối Y

        // Tọa độ vẽ trên Canvas
        public double Left => X - (Diameter / 2);
        public double Top => Y - (Diameter / 2);

        public PointCollection ContourPoints { get; set; }

        public double StrokeThickness => State == "OK" ? 1 : 2;

        public double Width { get; set; }
        public double Height { get; set; }
        public double AngleDegree { get; set; } // Góc xoay từ mô hình OBB
        public bool IsAiDetected { get; set; }
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