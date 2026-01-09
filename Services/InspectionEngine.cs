using OpenCvSharp; 
using System.Collections.Generic;
using WpfXrayQA.Models;
using System.Windows.Media;
using System.IO;

namespace WpfXrayQA.Services
{
    public sealed class InspectionEngine
    {
        private readonly YoloObbService _yoloService = new YoloObbService();
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
                    var directions = new OpenCvSharp.Point[]
                    {
                        new OpenCvSharp.Point(ball.X - pitch, ball.Y),
                        new OpenCvSharp.Point(ball.X + pitch, ball.Y),
                        new OpenCvSharp.Point(ball.X, ball.Y - pitch),
                        new OpenCvSharp.Point(ball.X, ball.Y + pitch)
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
        private bool IsLocationOccupied(OpenCvSharp.Point p, List<OverlayShape> balls, double radius)
        {
            foreach (var b in balls)
            {
                double dist = Math.Sqrt(Math.Pow(p.X - b.X, 2) + Math.Pow(p.Y - b.Y, 2));
                if (dist < radius) return true;
            }
            return false;
        }

        private bool IsValidBallAtLocation(Mat img, OpenCvSharp.Point p, Recipe recipe)
        {
            // Kiểm tra biên ảnh
            if (p.X < 0 || p.Y < 0 || p.X >= img.Width || p.Y >= img.Height) return false;

            // [FIX 1] Tăng kích thước vùng cắt (ROI) lên để bao trọn cả ball và viền nền
            // Trước đây là 0.8 (quá nhỏ), giờ tăng lên 1.3 để nhìn thấy được biên dạng
            int r = (int)(recipe.BallRadiusPx * 1.3);
            if (r < 5) r = 10;

            var imgRect = new OpenCvSharp.Rect(0, 0, img.Width, img.Height);
            var roiRect = new OpenCvSharp.Rect((int)p.X - r, (int)p.Y - r, r * 2, r * 2);
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

        // Trong file InspectionEngine.cs

        public List<OverlayShape> InspectFixedGridWithAlignment(string imagePath, Recipe recipe)
        {
            var resultShapes = new List<OverlayShape>();
            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return resultShapes;

            // 1. Tìm tất cả các Blob thực tế trên ảnh (Dùng làm mốc)
            var detectedBlobs = AutoDetectAllBallsOpenCV(imagePath, recipe);

            // Nếu không tìm thấy đủ ball, trả về lỗi ngay
            if (detectedBlobs.Count < 4) return FallbackToOriginal(recipe);

            // --- [THUẬT TOÁN MỚI] CENTROID ALIGNMENT (CĂN CHỈNH TRỌNG TÂM) ---

            // A. Tính trọng tâm của đám Ball thực tế trên ảnh
            double sumX = 0, sumY = 0;
            foreach (var b in detectedBlobs) { sumX += b.X; sumY += b.Y; }
            Point2f blobCenter = new Point2f((float)(sumX / detectedBlobs.Count), (float)(sumY / detectedBlobs.Count));

            // B. Tính trọng tâm của lưới Ball mẫu (Recipe)
            double refSumX = 0, refSumY = 0;
            foreach (var p in recipe.ReferencePoints) { refSumX += p.X; refSumY += p.Y; }
            Point2f refCenter = new Point2f((float)(refSumX / recipe.ReferencePoints.Count), (float)(refSumY / recipe.ReferencePoints.Count));

            // C. Tính vector dịch chuyển để 2 tâm trùng nhau
            float shiftToCenterX = blobCenter.X - refCenter.X;
            float shiftToCenterY = blobCenter.Y - refCenter.Y;

            // 2. LOGIC MATCHPOINTS: Thử ướm 4 hướng quanh TRỌNG TÂM MỚI
            double bestScore = -1;
            List<Point2f> bestSrcPoints = new List<Point2f>();
            List<Point2f> bestDstPoints = new List<Point2f>();

            double[] angles = { 0, 90, 180, 270 };

            foreach (var angle in angles)
            {
                // Tạo bản sao giả định: Dịch về tâm -> Xoay quanh tâm đó
                var rotatedRefs = new List<Point2f>();

                // Pre-calculate sin/cos
                double rad = angle * Math.PI / 180.0;
                float cos = (float)Math.Cos(rad);
                float sin = (float)Math.Sin(rad);

                foreach (var p in recipe.ReferencePoints)
                {
                    // Bước 1: Dịch chuyển điểm mẫu theo vector trọng tâm đã tính
                    // Để lưới mẫu nằm đè lên đám ball thực tế
                    float tempX = (float)p.X + shiftToCenterX;
                    float tempY = (float)p.Y + shiftToCenterY;

                    // Bước 2: Xoay điểm đó quanh trọng tâm (blobCenter)
                    float dx = tempX - blobCenter.X;
                    float dy = tempY - blobCenter.Y;

                    float xNew = blobCenter.X + (dx * cos - dy * sin);
                    float yNew = blobCenter.Y + (dx * sin + dy * cos);

                    rotatedRefs.Add(new Point2f(xNew, yNew));
                }

                // So khớp
                int currentMatchCount = 0;
                var tempSrc = new List<Point2f>();
                var tempDst = new List<Point2f>();

                // Dùng KD-Tree hoặc Grid Search sẽ nhanh hơn, nhưng Loop đơn giản vẫn ổn với < 2000 ball
                // Tối ưu: Chỉ so sánh những điểm nằm trong vùng bounding box
                for (int i = 0; i < rotatedRefs.Count; i++)
                {
                    var pRotated = rotatedRefs[i];

                    // Tìm ball gần nhất
                    var nearest = detectedBlobs.MinBy(b => Math.Pow(b.X - pRotated.X, 2) + Math.Pow(b.Y - pRotated.Y, 2));

                    if (nearest != null)
                    {
                        double dist = Math.Sqrt(Math.Pow(nearest.X - pRotated.X, 2) + Math.Pow(nearest.Y - pRotated.Y, 2));

                        // Nếu khoảng cách < 2.5 lần bán kính (giảm nhẹ radius để khắt khe hơn)
                        if (dist < recipe.BallRadiusPx * 2.5)
                        {
                            // Lưu cặp điểm GỐC (chưa dịch, chưa xoay) và điểm THỰC TẾ
                            var pOriginal = recipe.ReferencePoints[i];
                            tempSrc.Add(new Point2f((float)pOriginal.X, (float)pOriginal.Y));
                            tempDst.Add(new Point2f((float)nearest.X, (float)nearest.Y));
                            currentMatchCount++;
                        }
                    }
                }

                if (currentMatchCount > bestScore)
                {
                    bestScore = currentMatchCount;
                    bestSrcPoints = tempSrc;
                    bestDstPoints = tempDst;
                }
            }

            // 3. Tính toán Ma trận biến đổi cuối cùng
            // Yêu cầu số điểm khớp phải đạt ít nhất 10% tổng số ball (để tránh nhiễu)
            if (bestSrcPoints.Count > recipe.ReferencePoints.Count * 0.1)
            {
                try
                {
                    using var transformMatrix = Cv2.EstimateAffinePartial2D(
                        InputArray.Create(bestSrcPoints),
                        InputArray.Create(bestDstPoints));

                    if (transformMatrix != null && !transformMatrix.Empty())
                    {
                        var originalPoints = recipe.ReferencePoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
                        using var srcMat = Mat.FromArray(originalPoints);
                        using var dstMat = new Mat();

                        Cv2.Transform(srcMat, dstMat, transformMatrix);

                        Point2f[] transformedPoints;
                        dstMat.GetArray(out transformedPoints);

                        for (int i = 0; i < transformedPoints.Length; i++)
                        {
                            var pt = transformedPoints[i];
                            // Kiểm tra sắc độ tại vị trí mới
                            bool isBallPresent = CheckIntensityAtPoint(src, new Point(pt.X, pt.Y), recipe.BallRadiusPx, recipe.FixedThreshold, recipe.VoidThreshold);

                            resultShapes.Add(new OverlayShape
                            {
                                X = pt.X,
                                Y = pt.Y,
                                State = isBallPresent ? "OK" : "NG",
                                Diameter = recipe.BallRadiusPx * 2,
                                IsFoundByBlob = true
                            });
                        }

                        // --- KẾT THÚC XỬ LÝ ---
                        // Gọi thêm AI Check Short ở đây nếu cần
                        return resultShapes;
                    }
                }
                catch
                {
                    // Fallback nếu lỗi tính ma trận
                }
            }

            return FallbackToOriginal(recipe);
        }

        // Hàm dự phòng: Trả về tọa độ gốc nếu không căn chỉnh được
        private List<OverlayShape> FallbackToOriginal(Recipe recipe)
        {
            var list = new List<OverlayShape>();
            foreach (var p in recipe.ReferencePoints)
            {
                list.Add(new OverlayShape { X = p.X, Y = p.Y, State = "NG", Diameter = recipe.BallRadiusPx * 2 });
            }
            return list;
        }

        // Hàm kiểm tra độ tối tại 1 điểm (Cực nhanh & Đơn giản)
        private bool CheckIntensityAtPoint(Mat img, OpenCvSharp.Point center, int radius, double threshold, double voidThreshold)
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

            var roi = new OpenCvSharp.Rect(x, y, r * 2, r * 2);
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
            // Nếu hơn [voidThreshold]% diện tích vùng trung tâm là màu tối -> Xác nhận có Ball (OK)
            // Nếu vùng này chủ yếu là màu sáng (nền/void) -> NG
            bool isBallPresent = density > voidThreshold;

            return isBallPresent;
        }

        // File: InspectionEngine.cs

        // [Thay thế toàn bộ hàm DetectShortCircuits và IsBridgeDark cũ bằng đoạn này]

        public List<OverlayShape> DetectShortCircuits(Mat src, List<OverlayShape> balls, Recipe recipe)
        {
            var shorts = new List<OverlayShape>();
            if (balls.Count < 2) return shorts;

            // Khoảng cách để xét lân cận (3.5 lần bán kính ~ gần 2 ball cạnh nhau)
            // Lưu ý: Code cũ của bạn để * 7 là quá xa, dễ bắt nhầm các ball hàng khác
            double neighborDistThreshold = recipe.BallRadiusPx * 6.0;

            // Ngưỡng phân định: Dưới mức này là Vật thể (Ball/Short/Component), Trên mức này là Nền
            // Bạn nên chỉnh trong Recipe: Ball đen ~ 40, Nền trắng ~ 200.
            // Thì Threshold nên là khoảng 100-120 để an toàn (hoặc dùng giá trị FixedThreshold của bạn)
            double darkThreshold = recipe.FixedThreshold > 0 ? recipe.FixedThreshold : 100;

            // Nếu muốn khắt khe hơn cho cầu nối (cầu nối thường mờ hơn ball chút), có thể giảm nhẹ
            // double bridgeThreshold = darkThreshold * 0.9; 

            for (int i = 0; i < balls.Count; i++)
            {
                for (int j = i + 1; j < balls.Count; j++)
                {
                    var b1 = balls[i];
                    var b2 = balls[j];

                    // 1. Lọc theo khoảng cách
                    double dist = Math.Sqrt(Math.Pow(b1.X - b2.X, 2) + Math.Pow(b1.Y - b2.Y, 2));
                    if (dist > neighborDistThreshold) continue;

                    // 2. Tính toán điểm bắt đầu và kết thúc (ngay sát mép ball)
                    // Lấy sát mép (90% bán kính) để tránh kiểm tra phần thân ball
                    double margin = recipe.BallRadiusPx * 0.9;

                    // Nếu 2 ball quá gần (dính chồng lên nhau) -> Short chắc chắn
                    if (dist <= margin * 2)
                    {
                        AddShort(shorts, b1, b2, "Touch Overlap");
                        continue;
                    }

                    // 3. [THUẬT TOÁN MỚI] Quét từng pixel trên đường thẳng (Scan Line Profile)
                    if (IsContinuousDarkPath(src, new OpenCvSharp.Point(b1.X, b1.Y), new OpenCvSharp.Point(b2.X, b2.Y), margin, darkThreshold))
                    {
                        AddShort(shorts, b1, b2, "Bridge Detected");

                        // Đánh dấu NG
                        b1.State = "NG";
                        b2.State = "NG";
                    }
                }
            }
            return shorts;
        }

        // Hàm phụ trợ để thêm vào danh sách (cho gọn code)
        private void AddShort(List<OverlayShape> list, OverlayShape b1, OverlayShape b2, string msg)
        {
            list.Add(new OverlayShape
            {
                X = b1.X,
                Y = b1.Y,
                X2 = b2.X,
                Y2 = b2.Y,
                IsLine = true,
                State = "SHORT",
                TooltipInfo = msg
            });
        }

        // [THAY ĐỔI CỐT LÕI] Kiểm tra tính liên tục của đường đen
        private bool IsContinuousDarkPath(Mat img, OpenCvSharp.Point p1, OpenCvSharp.Point p2, double margin, double threshold)
        {
            // Tính vector chỉ phương
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);

            if (len == 0) return false;

            double ux = dx / len;
            double uy = dy / len;

            // Xác định đoạn thẳng cần quét (nằm giữa 2 ball)
            double startX = p1.X + ux * margin;
            double startY = p1.Y + uy * margin;

            // Quét đến sát mép ball kia
            double endX = p2.X - ux * margin;
            double endY = p2.Y - uy * margin;

            // Số bước nhảy (quét từng pixel một để không bỏ sót khe hở nào)
            double scanLength = len - (2 * margin);
            if (scanLength <= 0) return true; // Dính sát sạt

            int steps = (int)scanLength;

            // QUÉT TỪNG PIXEL
            for (int k = 0; k <= steps; k++)
            {
                int px = (int)(startX + (ux * k));
                int py = (int)(startY + (uy * k));

                // Check bounds
                if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) continue;

                // Lấy độ sáng pixel
                byte intensity = img.At<byte>(py, px);

                // [LOGIC QUAN TRỌNG]
                // Chỉ cần MỘT điểm sáng (Gap) xuất hiện -> Cắt đứt mạch -> Không phải Short
                // (Pixel sáng nghĩa là intensity > threshold)
                if (intensity > threshold)
                {
                    return false; // Phát hiện khe hở (nền), lập tức trả về False
                }
            }

            // Nếu chạy hết vòng lặp mà không gặp điểm sáng nào -> Đường nối liền mạch
            return true;
        }
    }
}