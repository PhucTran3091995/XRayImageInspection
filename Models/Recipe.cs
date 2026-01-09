using System.Text.Json.Serialization;

namespace WpfXrayQA.Models
{
    public sealed class Recipe
    {
        public string RecipeId { get; set; } = "";
        public string Model { get; set; } = "";
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }

        // --- CÁC THAM SỐ LỌC BLOB (AUTO DETECT) ---
        public int MinBallAreaPx { get; set; } = 50;
        public int MaxBallAreaPx { get; set; } = 3000;

        // [MỚI] Ngưỡng độ tròn (0.1 -> 1.0)
        public double MinCircularity { get; set; } = 0.1;

        // [MỚI] Chế độ Threshold
        public bool UseAdaptiveThreshold { get; set; } = true;
        public double AutoThreshold { get; set; } = 10;        // ParamC cho Adaptive
        public int FixedThreshold { get; set; } = 100;         // Giá trị cho Fixed Threshold

        // [MỚI] Kích thước nhân lọc nhiễu
        public int MorphKernelSize { get; set; } = 3;

        // [MỚI] Ngưỡng Void (0-100%) - Dưới ngưỡng này coi là Missing
        public double VoidThreshold { get; set; } = 30;

        // Các tham số cũ (Giữ lại để tránh lỗi tham chiếu khác)
        public int BallRadiusPx { get; set; } = 15;
        public int TargetBallCount { get; set; } = 1225;
        public double MissingMeanThreshold { get; set; } = 120;
        public double ShortBridgeMeanThreshold { get; set; } = 60;

        // Tọa độ 3 điểm (Dù không dùng Manual nữa vẫn nên giữ để không lỗi các hàm cũ)
        public double Ax { get; set; }
        public double Ay { get; set; }
        public double Bx { get; set; }
        public double By { get; set; }
        public double Cx { get; set; }
        public double Cy { get; set; }
        public int Rows { get; set; }
        public int Cols { get; set; }

        public (double dxX, double dxY, double dyX, double dyY) ComputeBasis()
        {
            var denomX = (Cols - 1) == 0 ? 1 : (Cols - 1);
            var denomY = (Rows - 1) == 0 ? 1 : (Rows - 1);
            var dxX = (Bx - Ax) / denomX;
            var dxY = (By - Ay) / denomX;
            var dyX = (Cx - Bx) / denomY;
            var dyY = (Cy - By) / denomY;
            return (dxX, dxY, dyX, dyY);
        }

        // Thêm 4 thuộc tính xác định vùng ROI
        public int RoiX { get; set; } = 0;
        public int RoiY { get; set; } = 0;
        public int RoiWidth { get; set; } = 0;
        public int RoiHeight { get; set; } = 0;
        
        // [MỚI] Tọa độ và kích thước vùng mẫu để so khớp (Template Matching)
        public int TemplateX { get; set; }
        public int TemplateY { get; set; }
        public int TemplateWidth { get; set; }
        public int TemplateHeight { get; set; }

        // Hàm kiểm tra xem có đang dùng ROI không (Nếu Width > 0 là có)
        public bool HasRoi() => RoiWidth > 0 && RoiHeight > 0;

        // Lưu danh sách tọa độ chuẩn từ ảnh Teach
        public List<System.Windows.Point> ReferencePoints { get; set; } = new();
    }
}