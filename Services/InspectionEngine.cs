using OpenCvSharp; 
using System.Collections.Generic;
using WpfXrayQA.Models;
using System.Windows.Media;

namespace WpfXrayQA.Services
{
    public sealed class InspectionEngine
    {
        public List<OverlayShape> AutoDetectAllBallsOpenCV(string imagePath, Recipe recipe)
        {
            var shapes = new List<OverlayShape>();

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return shapes;

            // 1. XỬ LÝ ROI AN TOÀN (Dùng hàm Intersect chuẩn của OpenCV)
            Mat processingImage = src;
            int offsetX = 0;
            int offsetY = 0;
            bool isCropped = false;

            if (recipe.HasRoi())
            {
                // Tạo Rect cho toàn bộ ảnh
                var imageRect = new Rect(0, 0, src.Width, src.Height);
                // Tạo Rect cho ROI người dùng vẽ
                var userRoi = new Rect(recipe.RoiX, recipe.RoiY, recipe.RoiWidth, recipe.RoiHeight);

                // Lấy phần giao nhau (Intersect)
                // Hàm này tự động xử lý các trường hợp tọa độ âm hoặc tràn viền
                var validRoi = imageRect.Intersect(userRoi);

                if (validRoi.Width > 0 && validRoi.Height > 0)
                {
                    processingImage = new Mat(src, validRoi);
                    offsetX = validRoi.X;
                    offsetY = validRoi.Y;
                    isCropped = true;
                }
            }

            try
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(processingImage, blurred, new Size(5, 5), 0);

                using var binary = new Mat();

                if (recipe.UseAdaptiveThreshold)
                {
                    int blockSize = 15;
                    if (recipe.MaxBallAreaPx > 0)
                    {
                        double r = Math.Sqrt(recipe.MaxBallAreaPx / Math.PI);
                        blockSize = (int)(r * 2) + 1;
                        if (blockSize < 3) blockSize = 3;
                        if (blockSize % 2 == 0) blockSize++;
                    }

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
                    Cv2.Threshold(blurred, binary, recipe.FixedThreshold, 255, ThresholdTypes.BinaryInv);
                }

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

                Cv2.FindContours(
                    morph,
                    out Point[][] contours,
                    out _,
                    RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple
                );

                foreach (var contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area < recipe.MinBallAreaPx || area > recipe.MaxBallAreaPx) continue;

                    double perimeter = Cv2.ArcLength(contour, true);
                    if (perimeter == 0) continue;
                    double circularity = (4 * Math.PI * area) / (perimeter * perimeter);

                    if (circularity >= recipe.MinCircularity)
                    {
                        var moments = Cv2.Moments(contour);
                        if (moments.M00 == 0) continue;

                        double localX = moments.M10 / moments.M00;
                        double localY = moments.M01 / moments.M00;

                        var wpfPoints = new PointCollection();
                        // Tối ưu hóa: Dùng epsilon lớn hơn (2.0) để giảm số điểm vẽ, giúp UI mượt hơn
                        var approxCurve = Cv2.ApproxPolyDP(contour, 2.0, true);

                        foreach (var p in approxCurve)
                        {
                            wpfPoints.Add(new System.Windows.Point(p.X + offsetX, p.Y + offsetY));
                        }

                        shapes.Add(new OverlayShape
                        {
                            X = localX + offsetX,
                            Y = localY + offsetY,
                            Diameter = Math.Sqrt(area / Math.PI) * 2,
                            IsFoundByBlob = true,
                            ContourPoints = wpfPoints
                        });
                    }
                }
            }
            finally
            {
                if (isCropped && processingImage != null)
                {
                    processingImage.Dispose();
                }
            }

            return shapes;
        }
        public List<OverlayShape> AdvancedGridSearch(string imagePath, Recipe recipe)
        {
            // 1. Chạy Blob Detect cơ bản
            var initialBalls = AutoDetectAllBallsOpenCV(imagePath, recipe);

            if (initialBalls.Count < 2) return initialBalls;

            // 2. Tính khoảng cách trung bình (Pitch)
            List<double> distances = new List<double>();
            foreach (var b1 in initialBalls)
            {
                double minD = double.MaxValue;
                foreach (var b2 in initialBalls)
                {
                    if (b1 == b2) continue;
                    double d = Math.Sqrt(Math.Pow(b1.X - b2.X, 2) + Math.Pow(b1.Y - b2.Y, 2));
                    if (d < minD) minD = d;
                }
                distances.Add(minD);
            }
            distances.Sort();
            double pitch = distances[distances.Count / 2];

            // Validate pitch
            if (pitch < recipe.BallRadiusPx) pitch = recipe.BallRadiusPx * 2.5;
            if (pitch <= 0) pitch = 20; // Giá trị fallback an toàn

            var allBalls = new List<OverlayShape>(initialBalls);
            var visited = new HashSet<(int, int)>();
            foreach (var b in initialBalls) visited.Add(((int)b.X, (int)b.Y));

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return allBalls;

            bool foundNew;
            int maxIterations = 50;

            do
            {
                foundNew = false;
                var newCandidates = new List<OverlayShape>();

                foreach (var ball in allBalls)
                {
                    var directions = new Point[]
                    {
                        new Point(ball.X - pitch, ball.Y),
                        new Point(ball.X + pitch, ball.Y),
                        new Point(ball.X, ball.Y - pitch),
                        new Point(ball.X, ball.Y + pitch)
                    };

                    foreach (var p in directions)
                    {
                        if (IsLocationOccupied(p, allBalls, pitch / 2)) continue;

                        if (IsValidBallAtLocation(src, p, recipe))
                        {
                            var newBall = new OverlayShape
                            {
                                X = p.X,
                                Y = p.Y,
                                Diameter = recipe.BallRadiusPx * 2,
                                IsFoundByBlob = false
                            };

                            newCandidates.Add(newBall);
                            foundNew = true;
                        }
                    }
                }

                if (foundNew)
                {
                    allBalls.AddRange(newCandidates);
                    maxIterations--;
                }

            } while (foundNew && maxIterations > 0);

            return allBalls;
        }

        // Hàm phụ trợ: Kiểm tra vị trí đã có ball chưa
        private bool IsLocationOccupied(Point p, List<OverlayShape> balls, double radius)
        {
            foreach (var b in balls)
            {
                double dist = Math.Sqrt(Math.Pow(p.X - b.X, 2) + Math.Pow(p.Y - b.Y, 2));
                if (dist < radius) return true;
            }
            return false;
        }

        private bool IsValidBallAtLocation(Mat img, Point p, Recipe recipe)
        {
            // Kiểm tra biên ảnh
            if (p.X < 0 || p.Y < 0 || p.X >= img.Width || p.Y >= img.Height) return false;

            // [FIX 1] Tăng kích thước vùng cắt (ROI) lên để bao trọn cả ball và viền nền
            // Trước đây là 0.8 (quá nhỏ), giờ tăng lên 1.3 để nhìn thấy được biên dạng
            int r = (int)(recipe.BallRadiusPx * 1.3);
            if (r < 5) r = 10;

            var imgRect = new Rect(0, 0, img.Width, img.Height);
            var roiRect = new Rect((int)p.X - r, (int)p.Y - r, r * 2, r * 2);
            var validRoi = imgRect.Intersect(roiRect);

            if (validRoi.Width <= 0 || validRoi.Height <= 0) return false;

            using var patch = new Mat(img, validRoi);

            // [FIX 2] Thay vì chỉ check độ sáng, ta chạy Blob Detect cục bộ
            using var binary = new Mat();
            // Dùng Fixed Threshold của Recipe để đồng bộ
            Cv2.Threshold(patch, binary, recipe.FixedThreshold, 255, ThresholdTypes.BinaryInv);

            Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (var contour in contours)
            {
                // 1. Kiểm tra Diện tích (Min Area)
                double area = Cv2.ContourArea(contour);
                if (area < recipe.MinBallAreaPx || area > recipe.MaxBallAreaPx) continue;

                // 2. Kiểm tra Độ tròn
                double perimeter = Cv2.ArcLength(contour, true);
                if (perimeter == 0) continue;
                double circularity = (4 * Math.PI * area) / (perimeter * perimeter);
                if (circularity < recipe.MinCircularity) continue;

                // 3. [QUAN TRỌNG] Kiểm tra xem Blob có nằm gọn trong ô không?
                // Nếu Blob chạm vào biên của vùng cắt (ROI), nghĩa là nó là một phần của vật thể khổng lồ (linh kiện đen) -> BỎ QUA
                var boundingRect = Cv2.BoundingRect(contour);
                bool touchesBorder = boundingRect.X <= 1 || boundingRect.Y <= 1 ||
                                     (boundingRect.Right >= patch.Width - 1) ||
                                     (boundingRect.Bottom >= patch.Height - 1);

                if (touchesBorder) continue;

                // Nếu vượt qua tất cả bài test -> Đây là ball thật
                return true;
            }

            return false;
        }

        // Trong file InspectionEngine.cs

        public List<OverlayShape> InspectFixedGridWithAlignment(string imagePath, Recipe recipe)
        {
            var resultShapes = new List<OverlayShape>();

            // 1. Nếu không có tọa độ mẫu thì trả về rỗng (hoặc chạy auto detect tùy bạn)
            if (recipe.ReferencePoints == null || recipe.ReferencePoints.Count == 0)
                return resultShapes;

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return resultShapes;

            // 2. [TÙY CHỌN] CĂN CHỈNH (ALIGNMENT)
            // Nếu bạn muốn chương trình thông minh hơn (tự bù trừ khi ảnh bị lệch), giữ đoạn code này.
            // Nếu bạn muốn nó "cứng nhắc" tuyệt đối (đúng tọa độ pixel), hãy set shiftX = 0, shiftY = 0.

            double shiftX = 0;
            double shiftY = 0;

            // --- Bắt đầu đoạn tính độ lệch ---
            var currentBlobs = AutoDetectAllBallsOpenCV(imagePath, recipe); // Scan nhanh để tìm mốc
            if (currentBlobs.Count > 0)
            {
                var deltasX = new List<double>();
                var deltasY = new List<double>();

                foreach (var refPt in recipe.ReferencePoints)
                {
                    // Tìm điểm thực tế gần nhất với điểm mẫu
                    var nearest = currentBlobs.MinBy(b => Math.Pow(b.X - refPt.X, 2) + Math.Pow(b.Y - refPt.Y, 2));
                    if (nearest != null)
                    {
                        double dist = Math.Sqrt(Math.Pow(nearest.X - refPt.X, 2) + Math.Pow(nearest.Y - refPt.Y, 2));
                        if (dist < recipe.BallRadiusPx) // Chỉ chấp nhận nếu lệch ít
                        {
                            deltasX.Add(nearest.X - refPt.X);
                            deltasY.Add(nearest.Y - refPt.Y);
                        }
                    }
                }
                // Lấy trung vị (Median) để ra độ lệch chuẩn xác nhất
                if (deltasX.Count > 0)
                {
                    deltasX.Sort(); deltasY.Sort();
                    shiftX = deltasX[deltasX.Count / 2];
                    shiftY = deltasY[deltasY.Count / 2];
                }
            }
            // --- Kết thúc đoạn tính độ lệch ---

            // 3. VẼ LẠI TOÀN BỘ BALL MẪU VÀ KIỂM TRA MÀU SẮC
            foreach (var refPt in recipe.ReferencePoints)
            {
                // Tọa độ kiểm tra = Tọa độ mẫu + Độ lệch (Alignment)
                double checkX = refPt.X + shiftX;
                double checkY = refPt.Y + shiftY;

                // Kiểm tra độ tối tại vị trí này
                bool isBallPresent = CheckIntensityAtPoint(src, new Point(checkX, checkY), recipe.BallRadiusPx, recipe.FixedThreshold);

                resultShapes.Add(new OverlayShape
                {
                    X = checkX,
                    Y = checkY,
                    Diameter = recipe.BallRadiusPx * 2,
                    State = isBallPresent ? "OK" : "NG", // Xanh hoặc Đỏ
                    TooltipInfo = isBallPresent ? "Pass" : "Missing/Void"
                });
            }

            return resultShapes;
        }

        // Hàm kiểm tra độ tối tại 1 điểm (Cực nhanh & Đơn giản)
        private bool CheckIntensityAtPoint(Mat img, Point center, int radius, double threshold)
        {
            // 1. Cắt vùng trung tâm của Ball (khoảng 60% bán kính)
            // Việc cắt nhỏ giúp loại bỏ ảnh hưởng của viền ball dính vào nền
            int r = (int)(radius * 0.6);
            if (r < 2) r = 2;

            int x = (int)center.X - r;
            int y = (int)center.Y - r;

            // Kiểm tra biên ảnh an toàn
            if (x < 0 || y < 0 || x + 2 * r > img.Width || y + 2 * r > img.Height)
                return false;

            var roi = new Rect(x, y, r * 2, r * 2);
            using var patch = new Mat(img, roi);

            // 2. PHÂN NGƯỠNG CỤC BỘ (Binarization)
            // Thay vì tính trung bình màu, ta đếm xem có bao nhiêu pixel thực sự ĐEN
            using var binary = new Mat();
            // Chân ball tối (đen) sẽ trở thành màu trắng (255) trong ảnh BinaryInv
            Cv2.Threshold(patch, binary, threshold + 20, 255, ThresholdTypes.BinaryInv);

            // 3. TÍNH MẬT ĐỘ ĐIỂM ĐEN (Pixel Density)
            // CountNonZero sẽ đếm số pixel thỏa mãn độ tối
            int blackPixels = Cv2.CountNonZero(binary);
            double totalPixels = roi.Width * roi.Height;
            double density = (blackPixels / totalPixels) * 100; // Đơn vị %

            // CHIẾN THUẬT PHÁN ĐỊNH:
            // Nếu hơn 70% diện tích vùng trung tâm là màu tối -> Xác nhận có Ball (OK)
            // Nếu vùng này chủ yếu là màu sáng (nền/void) -> NG
            bool isBallPresent = density > 50;

            return isBallPresent;
        }
    }
}