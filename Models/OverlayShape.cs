namespace WpfXrayQA.Models
{
    public class OverlayShape
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Diameter { get; set; }
        public bool IsFoundByBlob { get; set; }

        // Tọa độ vẽ trên Canvas
        public double Left => X - (Diameter / 2);
        public double Top => Y - (Diameter / 2);

        // Nội dung Tooltip để bạn xem diện tích và tọa độ
        public string TooltipInfo => $"X:{X:F1}, Y:{Y:F1}\nArea: {Math.PI * Math.Pow(Diameter / 2, 2):F1} px";
    }
}