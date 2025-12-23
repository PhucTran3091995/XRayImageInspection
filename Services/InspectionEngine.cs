using OpenCvSharp; // Cần cài đặt NuGet OpenCvSharp4
using System.Collections.Generic;
using WpfXrayQA.Models;

namespace WpfXrayQA.Services
{
    public sealed class InspectionEngine
    {
        public List<OverlayShape> AutoDetectAllBallsOpenCV(string imagePath, Recipe recipe)
        {
            var shapes = new List<OverlayShape>();

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return shapes;

            // 1. Blur nhẹ
            using var blurred = new Mat();
            Cv2.GaussianBlur(src, blurred, new Size(5, 5), 0);

            using var binary = new Mat();

            // 2. THRESHOLD: Cho phép chọn giữa Adaptive và Fixed
            if (recipe.UseAdaptiveThreshold)
            {
                int blockSize = recipe.BallRadiusPx * 2 + 1;
                if (blockSize < 3) blockSize = 3;
                if (blockSize % 2 == 0) blockSize++;

                Cv2.AdaptiveThreshold(
                    blurred,
                    binary,
                    255,
                    AdaptiveThresholdTypes.MeanC,
                    ThresholdTypes.BinaryInv,
                    blockSize,
                    recipe.AutoThreshold > 0 ? recipe.AutoThreshold : 10
                );
            }
            else
            {
                // Fixed Threshold: Dùng cho trường hợp ball quá méo hoặc tương phản kém
                Cv2.Threshold(blurred, binary, recipe.FixedThreshold, 255, ThresholdTypes.BinaryInv);
            }

            // 3. MORPHOLOGY: Điều chỉnh nhân để không ăn mất ball méo
            // Nếu MorphKernelSize <= 1 thì bỏ qua bước này (giữ nguyên hình dạng gốc)
            using var morph = new Mat();
            if (recipe.MorphKernelSize > 1)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(recipe.MorphKernelSize, recipe.MorphKernelSize));
                Cv2.MorphologyEx(binary, morph, MorphTypes.Open, kernel);
            }
            else
            {
                binary.CopyTo(morph);
            }

            // 4. FIND CONTOURS
            Cv2.FindContours(
                morph,
                out Point[][] contours,
                out HierarchyIndex[] hierarchy,
                RetrievalModes.External,
                ContourApproximationModes.ApproxNone
            );

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < recipe.MinBallAreaPx || area > recipe.MaxBallAreaPx) continue;

                double perimeter = Cv2.ArcLength(contour, true);
                if (perimeter == 0) continue;

                // Tính độ tròn
                double circularity = (4 * Math.PI * area) / (perimeter * perimeter);

                // Lọc độ tròn (Nếu MinCircularity = 0 thì lấy hết)
                if (circularity >= recipe.MinCircularity)
                {
                    var moments = Cv2.Moments(contour);
                    if (moments.M00 == 0) continue;

                    double cx = moments.M10 / moments.M00;
                    double cy = moments.M01 / moments.M00;

                    shapes.Add(new OverlayShape
                    {
                        X = cx,
                        Y = cy,
                        Diameter = Math.Sqrt(area / Math.PI) * 2,
                        IsFoundByBlob = true
                    });
                }
            }

            return shapes;
        }
    }
}