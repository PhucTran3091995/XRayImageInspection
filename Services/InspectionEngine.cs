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

            if (recipe.ReferencePoints == null || recipe.ReferencePoints.Count == 0)
                return resultShapes;

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return resultShapes;

            // =========================================================
            // BƯỚC 1: DÒ TÌM CÁC ĐIỂM MỐC (RELAXED SEARCH)
            // =========================================================
            // Tạo Recipe dễ tính để bắt được ball ngay cả khi bị rung/méo
            var alignRecipe = new Recipe
            {
                MinBallAreaPx = (int)(recipe.MinBallAreaPx * 0.5), // Chấp nhận nhỏ hơn
                MaxBallAreaPx = (int)(recipe.MaxBallAreaPx * 1.5), // Chấp nhận to hơn (do zoom)
                MinCircularity = 0.4, // Chấp nhận méo
                UseAdaptiveThreshold = true,
                AutoThreshold = recipe.AutoThreshold,
                MorphKernelSize = recipe.MorphKernelSize,
                BallRadiusPx = recipe.BallRadiusPx,
                // Copy vùng ROI
                RoiX = recipe.RoiX,
                RoiY = recipe.RoiY,
                RoiWidth = recipe.RoiWidth,
                RoiHeight = recipe.RoiHeight
            };

            var currentBlobs = AutoDetectAllBallsOpenCV(imagePath, alignRecipe);

            // =========================================================
            // BƯỚC 2: TÍNH TOÁN MA TRẬN BIẾN ĐỔI (SHIFT + ZOOM + ROTATE)
            // =========================================================

            // Danh sách cặp điểm: Nguồn (Recipe) -> Đích (Thực tế)
            var srcPoints = new List<Point2f>();
            var dstPoints = new List<Point2f>();

            if (currentBlobs.Count > 0)
            {
                foreach (var refPt in recipe.ReferencePoints)
                {
                    // Tìm điểm thực tế gần nhất trong phạm vi cho phép
                    // Cho phép lệch xa hơn (3 lần bán kính) để bắt được các điểm ở góc bị Zoom mạnh
                    double searchRadius = recipe.BallRadiusPx * 3.0;

                    var nearest = currentBlobs.MinBy(b => Math.Pow(b.X - refPt.X, 2) + Math.Pow(b.Y - refPt.Y, 2));

                    if (nearest != null)
                    {
                        double dist = Math.Sqrt(Math.Pow(nearest.X - refPt.X, 2) + Math.Pow(nearest.Y - refPt.Y, 2));
                        if (dist < searchRadius)
                        {
                            srcPoints.Add(new Point2f((float)refPt.X, (float)refPt.Y));
                            dstPoints.Add(new Point2f((float)nearest.X, (float)nearest.Y));
                        }
                    }
                }
            }

            // Biến lưu trữ các điểm tham chiếu sau khi đã được căn chỉnh (biến đổi)
            Point2f[] transformedPoints;

            // Cần ít nhất 4 cặp điểm để tính toán ma trận biến đổi Affine
            if (srcPoints.Count >= 4)
            {
                // [THUẬT TOÁN CỐT LÕI]: EstimateAffinePartial2D
                using var inliers = new Mat();

                // Sử dụng InputArray.Create hoặc Mat.FromArray để đảm bảo đúng kiểu dữ liệu
                using var transformMatrix = Cv2.EstimateAffinePartial2D(
                        InputArray.Create(srcPoints.ToArray()),
                        InputArray.Create(dstPoints.ToArray()),
                        inliers);

                if (!transformMatrix.Empty())
                {
                    // Chuyển đổi toàn bộ ReferencePoints gốc sang Mat
                    var originalPoints = recipe.ReferencePoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();

                    using var srcMat = Mat.FromArray(originalPoints);
                    using var dstMat = new Mat();

                    // Cv2.Transform(src, dst, matrix) trả về void, kết quả nằm trong dst
                    Cv2.Transform(srcMat, dstMat, transformMatrix);

                    // Chuyển kết quả từ Mat về mảng Point2f
                    transformedPoints = new Point2f[originalPoints.Length];
                    dstMat.GetArray(out transformedPoints);
                }
                else
                {
                    // Nếu không tính được ma trận, dùng tọa độ gốc
                    transformedPoints = recipe.ReferencePoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
                }
            }
            else
            {
                // Fallback: Nếu không tìm thấy đủ điểm tương đồng, giữ nguyên tọa độ gốc
                transformedPoints = recipe.ReferencePoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
            }

            // =========================================================
            // BƯỚC 3: KIỂM TRA TẠI TỌA ĐỘ MỚI (ĐÃ CĂN CHỈNH)
            // =========================================================
            for (int i = 0; i < transformedPoints.Length; i++)
            {
                var pt = transformedPoints[i];

                // Kiểm tra độ tối (Intensity) tại vị trí đã được bù trừ (Zoom/Shift)
                bool isBallPresent = CheckIntensityAtPoint(src, new Point(pt.X, pt.Y), recipe.BallRadiusPx, recipe.FixedThreshold);

                resultShapes.Add(new OverlayShape
                {
                    X = pt.X,
                    Y = pt.Y,
                    Diameter = recipe.BallRadiusPx * 2,
                    State = isBallPresent ? "OK" : "NG",
                    // Hiển thị thông tin tọa độ để debug nếu cần
                    TooltipInfo = isBallPresent ? $"OK ({pt.X:F0},{pt.Y:F0})" : "Missing"
                });
            }

            return resultShapes;
        }
        /*public List<OverlayShape> InspectFixedGridWithAlignment(string imagePath, Recipe recipe)
        {
            var resultShapes = new List<OverlayShape>();

            // 1. Kiểm tra dữ liệu đầu vào
            if (recipe.ReferencePoints == null || recipe.ReferencePoints.Count == 0)
                return resultShapes;

            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return resultShapes;

            // =========================================================
            // BƯỚC 1: TỰ ĐỘNG CĂN CHỈNH (ALIGNMENT)
            // =========================================================
            double shiftX = 0;
            double shiftY = 0;

            // Tạo một Recipe "Dễ tính" (Relaxed) để dò tìm ball ngay cả khi ảnh bị rung/mờ
            var alignRecipe = new Recipe
            {
                // Copy các thông số quan trọng
                MinBallAreaPx = (int)(recipe.MinBallAreaPx * 0.8), // Cho phép nhỏ hơn chút
                MaxBallAreaPx = (int)(recipe.MaxBallAreaPx * 1.2), // Cho phép to hơn chút (do nhòe)

                // [QUAN TRỌNG] Giảm độ tròn xuống thấp để bắt được ball bị méo do rung
                MinCircularity = 0.4,

                // Luôn dùng Adaptive để bắt điểm tốt nhất
                UseAdaptiveThreshold = true,
                AutoThreshold = recipe.AutoThreshold,
                MorphKernelSize = recipe.MorphKernelSize,
                BallRadiusPx = recipe.BallRadiusPx,

                // ROI giữ nguyên
                RoiX = recipe.RoiX,
                RoiY = recipe.RoiY,
                RoiWidth = recipe.RoiWidth,
                RoiHeight = recipe.RoiHeight
            };

            // Quét nhanh để tìm các điểm mốc hiện tại
            var currentBlobs = AutoDetectAllBallsOpenCV(imagePath, alignRecipe);

            if (currentBlobs.Count > 0)
            {
                var deltasX = new List<double>();
                var deltasY = new List<double>();

                // Duyệt qua tất cả các điểm mẫu (Reference Points)
                foreach (var refPt in recipe.ReferencePoints)
                {
                    // Tìm điểm thực tế gần nhất với điểm mẫu
                    var nearest = currentBlobs.MinBy(b => Math.Pow(b.X - refPt.X, 2) + Math.Pow(b.Y - refPt.Y, 2));

                    if (nearest != null)
                    {
                        double dist = Math.Sqrt(Math.Pow(nearest.X - refPt.X, 2) + Math.Pow(nearest.Y - refPt.Y, 2));

                        // [CẢI TIẾN] Tăng phạm vi tìm kiếm lên 2.0 lần bán kính (thay vì 1.0)
                        // Giúp bắt được các trường hợp bị trôi xa do rung lắc mạnh
                        if (dist < recipe.BallRadiusPx * 2.0)
                        {
                            deltasX.Add(nearest.X - refPt.X);
                            deltasY.Add(nearest.Y - refPt.Y);
                        }
                    }
                }

                // Lấy trung vị (Median) để loại bỏ nhiễu
                if (deltasX.Count > 0)
                {
                    deltasX.Sort();
                    deltasY.Sort();

                    // Lấy giá trị giữa danh sách (Median) là độ lệch đáng tin cậy nhất
                    shiftX = deltasX[deltasX.Count / 2];
                    shiftY = deltasY[deltasY.Count / 2];
                }
            }

            // =========================================================
            // BƯỚC 2: VẼ LẠI VÀ KIỂM TRA (INSPECTION)
            // =========================================================
            foreach (var refPt in recipe.ReferencePoints)
            {
                // Áp dụng độ lệch vừa tính được
                double checkX = refPt.X + shiftX;
                double checkY = refPt.Y + shiftY;

                // Kiểm tra OK/NG tại vị trí mới
                // Lưu ý: Dùng recipe gốc (nghiêm ngặt) để kiểm tra độ tối
                bool isBallPresent = CheckIntensityAtPoint(src, new Point(checkX, checkY), recipe.BallRadiusPx, recipe.FixedThreshold);

                resultShapes.Add(new OverlayShape
                {
                    X = checkX,
                    Y = checkY,
                    Diameter = recipe.BallRadiusPx * 2, // Kích thước hiển thị theo chuẩn
                    State = isBallPresent ? "OK" : "NG",
                    TooltipInfo = isBallPresent ? $"Pass (Shift: {shiftX:F1}, {shiftY:F1})" : "Missing/Void"
                });
            }

            return resultShapes;
        }*/

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