using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfXrayQA.Models;
using WpfXrayQA.Services;

namespace WpfXrayQA.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly FileScannerService _scanner;
        private readonly DatabaseService _db;
        private readonly InspectionEngine _engine;
        private readonly RecipeStore _recipeStore;
        private readonly CsvInspectionLog _csvLog;
        private System.Timers.Timer _timer;

        // --- UI Properties ---
        [ObservableProperty] private BitmapImage? currentImage;
        [ObservableProperty] private PendingFileItem? selectedFile;
        [ObservableProperty] private string settingRootPaths = @"D:\FakeXRayData";
        [ObservableProperty] private string messageStatus = "Ready";

        // Manual Input
        [ObservableProperty] private string comment = "";
        [ObservableProperty] private bool isShort;
        [ObservableProperty] private bool isMissing;
        [ObservableProperty] private bool isBoth;

        // --- Teach Properties ---
        [ObservableProperty] private string teachStatus = "Ready to teach";
        [ObservableProperty] private int teachRows = 10;
        [ObservableProperty] private int teachCols = 10;
        [ObservableProperty] private int teachRadius = 15;

        // Add these properties to your MainViewModel class
        [ObservableProperty] private double teachCx;
        [ObservableProperty] private double teachCy;

        // Thresholds (Ball Đen)
        [ObservableProperty] private double teachMissingThres = 120; // > 120 là sáng (mất ball)
        [ObservableProperty] private double teachBridgeThres = 60;   // < 60 là tối (dính thiếc)

        [ObservableProperty] private int minArea = 50;   // Diện tích tối thiểu để lọc ball nhỏ
        [ObservableProperty] private int maxArea = 1500; // Diện tích tối đa để lọc linh kiện to
        [ObservableProperty] private int adaptiveBlockSize = 21; // Kích thước vùng tính ngưỡng thích nghi
        [ObservableProperty] private int adaptiveParamC = 10;
        [ObservableProperty] private double minCircularity = 0.5; // Độ tròn tối thiểu
        // Threshold
        [ObservableProperty] private bool useAdaptive = true;
        [ObservableProperty] private int fixedThresholdVal = 100;

        // Morphology
        [ObservableProperty] private int morphKernel = 3; // Mặc định nhỏ (3) để ít làm biến dạng

        // Số lượng
        [ObservableProperty] private int targetCount = 1225;

        // --- CÁC THUỘC TÍNH HIỂN THỊ KẾT QUẢ ---
        [ObservableProperty] private string resultDecision = "N/A"; // "OK" hoặc "NG"
        [ObservableProperty] private string resultDetail = "";      // Chi tiết lỗi (VD: Thiếu 2 ball)
        [ObservableProperty] private Brush resultColor = Brushes.Gray; // Màu nền (Xanh/Đỏ)

        private Recipe _tempRecipe = new();

        // [MỚI] Tên chương trình đang chạy
        [ObservableProperty] private string activeRecipeName = "Chưa chọn Program";
        private Recipe? _activeManualRecipe = null;

        // --- CÁC BIẾN CHO ROI ---
        [ObservableProperty] private bool isDrawMode = false; // Chế độ vẽ
        [ObservableProperty] private double roiLeft;
        [ObservableProperty] private double roiTop;
        [ObservableProperty] private double roiWidth;
        [ObservableProperty] private double roiHeight;
        [ObservableProperty] private Visibility roiVisibility = Visibility.Collapsed;

        [ObservableProperty] private double teachDiameter = 30; // Mặc định 30px (Radius = 15)

        // --- 1. THÊM THUỘC TÍNH TÊN RECIPE ĐỂ NHẬP TAY ---
        [ObservableProperty] private string newRecipeName = "";


/*        // 1. Tự động chạy lại Scan khi thay đổi thông số Threshold
        partial void OnFixedThresholdValChanged(int value) => AutoDetectBalls();
        partial void OnAdaptiveParamCChanged(int value) => AutoDetectBalls();
        partial void OnUseAdaptiveChanged(bool value) => AutoDetectBalls();

        // 2. Tự động chạy lại khi thay đổi bộ lọc hình dạng
        partial void OnMinAreaChanged(int value) => AutoDetectBalls();
        partial void OnMaxAreaChanged(int value) => AutoDetectBalls();
        partial void OnMinCircularityChanged(double value) => AutoDetectBalls();

        // 3. Tự động chạy lại khi thay đổi vùng ROI
        partial void OnRoiWidthChanged(double value) => AutoDetectBalls();
        partial void OnRoiHeightChanged(double value) => AutoDetectBalls();*/

        private Point _startPoint; // Điểm bắt đầu click chuột

        // [MỚI] View để hỗ trợ lọc (Filter) danh sách Pending
        public ICollectionView PendingFilesView { get; }

        // Collections
        public ObservableCollection<PendingFileItem> PendingFiles { get; } = new();
        public ObservableCollection<OverlayShape> GridOverlayShapes { get; } = new();
        public ObservableCollection<OverlayShape> TeachOverlayShapes { get; } = new();

        public MainViewModel()
        {
            _scanner = new FileScannerService();
            _db = new DatabaseService();
            _engine = new InspectionEngine();
            _recipeStore = new RecipeStore();
            _csvLog = new CsvInspectionLog();

            // Timer quét 3 giây/lần
            _timer = new System.Timers.Timer(3000);
            _timer.Elapsed += async (s, e) => await ScanCycle();
            _timer.Start();

            // Khởi tạo View cho danh sách PendingFiles để hỗ trợ lọc
            PendingFilesView = CollectionViewSource.GetDefaultView(PendingFiles);

        }

        // --- 1. Scanning Logic ---
        private bool _isScanning = false;
        private async Task ScanCycle()
        {
            // Nếu đang quét dở thì bỏ qua lượt timer này
            if (_isScanning) return;
            _isScanning = true;

            try
            {
                var roots = SettingRootPaths.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var root in roots)
                {
                    var files = await _scanner.ScanRootAsync(root.Trim());
                    foreach (var file in files)
                    {
                        var info = new FileInfo(file);
                        // Kiểm tra DB (An toàn vì DB service thường thread-safe hoặc local)
                        bool inDb = _db.IsReviewed(info.FullName, info.LastWriteTimeUtc, info.Length);

                        // 2. SỬA LỖI QUAN TRỌNG:
                        // Đưa việc kiểm tra 'Any' (ĐỌC) về UI Thread để tránh xung đột với việc Insert (GHI)
                        bool inQueue = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            inQueue = PendingFiles.Any(x => x.FullPath == file);
                        });

                        if (!inDb && !inQueue)
                        {
                            var newItem = new PendingFileItem(file);

                            // Chạy Auto Inspect (giữ nguyên async để không đơ UI)
                            await RunAutoInspection(newItem);

                            // Insert vào UI (Đã có Dispatcher như cũ)
                            Application.Current.Dispatcher.Invoke(() => PendingFiles.Insert(0, newItem));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Ghi log lỗi nếu cần
                System.Diagnostics.Debug.WriteLine($"Scan Error: {ex.Message}");
            }
            finally
            {
                // Mở khóa để cho phép lượt quét tiếp theo
                _isScanning = false;
            }
        }

        private async Task RunAutoInspection(PendingFileItem item)
        {
            await Task.Run(() =>
            {
                try
                {
                    string recipeId = item.ModelName;
                    if (_recipeStore.TryLoad(recipeId, out var recipe) && recipe != null)
                    {
                        // Replace _engine.Inspect with your own inspection logic or method
                        // For example, if you want to use AutoDetectAllBallsOpenCV:
                        var detectedBalls = _engine.AutoDetectAllBallsOpenCV(item.FullPath, recipe);
                        // You need to process detectedBalls and set the item's properties accordingly
                        item.AutoInspected = true;
                        item.AutoDecision = detectedBalls.Count > 0 ? "OK" : "NO_BALLS";
                        item.AutoDefectType = "N/A";
                        item.AutoMissingCount = 0; // Set based on your logic
                        item.AutoShortCount = 0;   // Set based on your logic
                    }
                    else
                    {
                        item.AutoInspected = true;
                        item.AutoDecision = "NO_RECIPE";
                    }
                    // Ghi log CSV ngay
                    _csvLog.Append(item);
                }
                catch
                {
                    item.AutoDecision = "ERROR";
                }
            });
        }
        partial void OnSelectedFileChanged(PendingFileItem? value)
        {
            if (value != null && File.Exists(value.FullPath))
            {
                LoadImageSafe(value.FullPath);

                // Ưu tiên dùng Recipe đang kích hoạt (đã Load Program hoặc vừa Save)
                var recipeToUse = _activeManualRecipe;

                // Nếu Recipe có điểm chuẩn (ReferencePoints) -> Chạy chế độ CHECK LƯỚI (Xanh/Đỏ)
                if (recipeToUse != null && recipeToUse.ReferencePoints != null && recipeToUse.ReferencePoints.Count > 0)
                {
                    try
                    {
                        // [FIX QUAN TRỌNG] Gọi hàm kiểm tra theo tọa độ cố định
                        var resultShapes = _engine.InspectFixedGridWithAlignment(value.FullPath, recipeToUse);

                        // Cập nhật lên Main Review
                        GridOverlayShapes.Clear();
                        foreach (var p in resultShapes) GridOverlayShapes.Add(p);

                        // Đếm lỗi để hiển thị Text
                        int ngCount = resultShapes.Count(s => s.State == "NG");
                        int okCount = resultShapes.Count(s => s.State == "OK");

                        NewRecipeName = value.ModelName;

                        if (ngCount == 0 && okCount >= recipeToUse.TargetBallCount)
                        {
                            MessageStatus = $"OK | Đủ {okCount} balls";
                            // Cập nhật màu nền cho ListBoxItem (nếu cần logic phức tạp hơn thì update vào item)
                        }
                        else
                        {
                            MessageStatus = $"NG | Thiếu {ngCount} balls";
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageStatus = $"Lỗi Check: {ex.Message}";
                    }
                }
                else
                {
                    // Nếu chưa có Recipe chuẩn -> Chạy Auto Detect (Preview)
                    // Lúc này tất cả sẽ màu XANH (vì chưa có chuẩn để so sánh)
                    try
                    {
                        var points = _engine.AutoDetectAllBallsOpenCV(value.FullPath, _tempRecipe);
                        GridOverlayShapes.Clear();
                        foreach (var p in points) GridOverlayShapes.Add(p);
                        MessageStatus = $"Preview Mode: Found {points.Count} balls (No Recipe)";
                    }
                    catch { }
                }

                // Luôn clear màn hình Teach để tránh nhầm lẫn
                TeachOverlayShapes.Clear();
            }
        }

        private void LoadImageSafe(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Quan trọng để dùng được ở thread khác
                }
                CurrentImage = bitmap;
            }
            catch (Exception ex)
            {
                MessageStatus = "Error loading image: " + ex.Message;
            }
        }

        // --- 3. Manual Review Commands ---
        [RelayCommand] private void SubmitOk() => ProcessDecision("OK", "NA");
        [RelayCommand] private void SubmitNg() => ProcessDecision("NG", IsShort ? "Short" : IsMissing ? "Missing" : IsBoth ? "Both" : "Unknown");
        [RelayCommand] private void SubmitRerun() => ProcessDecision("RERUN", "NA");

        [RelayCommand]
        private async Task SaveSettings()
        {
            MessageStatus = "Settings Saved. Rescanning...";
            await ScanCycle();
            MessageStatus = "Done.";
        }

        private void ProcessDecision(string decision, string defect)
        {
            if (SelectedFile == null) return;
            var info = new FileInfo(SelectedFile.FullPath);

            // Lưu vào DB
            _db.SaveLog(new ReviewLog
            {
                FullPath = SelectedFile.FullPath,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                FileSize = info.Length,
                DateFolder = DateTime.Now.ToString("yyyyMMdd"),
                ModelName = SelectedFile.ModelName,
                Filename = SelectedFile.FileName,
                ReviewedAt = DateTime.Now,
                Reviewer = "QA_User",
                Decision = decision,
                DefectType = defect,
                Comment = Comment
            });

            // Clean UI
            PendingFiles.Remove(SelectedFile);
            CurrentImage = null;
            GridOverlayShapes.Clear();

            // Auto Select Next
            if (PendingFiles.Any()) SelectedFile = PendingFiles.First();
        }

        [RelayCommand]
        private void SaveRecipe()
        {
            // 1. Kiểm tra: Người dùng đã nhập tên vào ô "Recipe Name" chưa?
            if (string.IsNullOrWhiteSpace(NewRecipeName))
            {
                MessageBox.Show("Vui lòng nhập tên Recipe (Model Name) vào ô bên dưới trước khi lưu!",
                                "Thiếu tên", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Kiểm tra: Đã có ảnh để làm mẫu chưa?
            if (SelectedFile == null && CurrentImage == null)
            {
                MessageBox.Show("Vui lòng chọn một file ảnh để làm mẫu.", "Chưa chọn ảnh");
                return;
            }

            try
            {
                // [SỬA LỖI] Chỉ lấy tên từ ô nhập liệu (NewRecipeName)
                // Đã XÓA dòng lấy từ SelectedFile.ModelName gây lỗi ghi đè
                _tempRecipe.RecipeId = NewRecipeName.Trim();
                _tempRecipe.Model = NewRecipeName.Trim();

                // 3. Lưu thông số bộ lọc hình dạng (Shape Filters)
                _tempRecipe.MinBallAreaPx = MinArea;
                _tempRecipe.MaxBallAreaPx = MaxArea;
                _tempRecipe.MinCircularity = MinCircularity;

                // 4. Lưu thông số ngưỡng sáng (Threshold)
                _tempRecipe.UseAdaptiveThreshold = UseAdaptive;
                _tempRecipe.AutoThreshold = AdaptiveParamC;
                _tempRecipe.FixedThreshold = FixedThresholdVal;

                // 5. Lưu thông số nâng cao & Kích thước Ball
                _tempRecipe.MorphKernelSize = MorphKernel;
                _tempRecipe.BallRadiusPx = (int)(TeachDiameter / 2);

                // 6. Lưu thông tin ROI (Vùng quan tâm)
                _tempRecipe.RoiX = (int)RoiLeft;
                _tempRecipe.RoiY = (int)RoiTop;
                _tempRecipe.RoiWidth = (int)RoiWidth;
                _tempRecipe.RoiHeight = (int)RoiHeight;

                // 7. Lưu kích thước ảnh gốc
                if (CurrentImage != null)
                {
                    _tempRecipe.ImageWidth = (int)CurrentImage.PixelWidth;
                    _tempRecipe.ImageHeight = (int)CurrentImage.PixelHeight;
                }

                // 8. Lấy tọa độ ball từ màn hình Teach
                if (TeachOverlayShapes.Count > 0)
                {
                    _tempRecipe.ReferencePoints = TeachOverlayShapes
                        .Select(shape => new Point(shape.X, shape.Y))
                        .ToList();

                    _tempRecipe.TargetBallCount = _tempRecipe.ReferencePoints.Count;
                    TargetCount = _tempRecipe.TargetBallCount; // Cập nhật ngược lại UI
                }
                else
                {
                    if (MessageBox.Show("Danh sách Teach đang trống. Bạn có chắc muốn lưu Recipe rỗng?",
                                        "Cảnh báo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                        return;

                    _tempRecipe.ReferencePoints = new List<Point>();
                    _tempRecipe.TargetBallCount = 0;
                }

                // 9. Thực hiện lưu xuống file JSON
                _recipeStore.Save(_tempRecipe);

                // [TỐI ƯU WORKFLOW] Cập nhật ngay Recipe hiện hành
                _activeManualRecipe = _tempRecipe;
                ActiveRecipeName = _tempRecipe.RecipeId;

                // 10. Thông báo thành công (Hiển thị đúng tên vừa lưu)
                MessageBox.Show($"Đã lưu Recipe thành công!\n" +
                                $"- Model: {_tempRecipe.RecipeId}\n" +
                                $"- Số lượng Ball chuẩn: {_tempRecipe.TargetBallCount}\n" +
                                $"- Bán kính Ball: {_tempRecipe.BallRadiusPx}px",
                                "Lưu thành công", MessageBoxButton.OK, MessageBoxImage.Information);

                // Cập nhật trạng thái thanh Status Bar
                TeachStatus = $"Recipe Saved: {_tempRecipe.RecipeId}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu Recipe: {ex.Message}", "Lỗi Critical",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void RemoveOverlay(OverlayShape item)
        {
            if (item == null) return;

            // 1. Ưu tiên kiểm tra và xóa trong danh sách TEACH (quan trọng nhất)
            if (TeachOverlayShapes.Contains(item))
            {
                TeachOverlayShapes.Remove(item);

                // Tính toán lại kết quả OK/NG sau khi xóa thủ công
                // Nếu xóa bớt ball thừa -> FoundCount giảm -> Có thể thành OK
                AnalyzeResult(TeachOverlayShapes.Count, TargetCount);
            }
            // 2. (Tùy chọn) Nếu sau này bạn muốn xóa cả ở màn Main Review
            else if (GridOverlayShapes.Contains(item))
            {
                GridOverlayShapes.Remove(item);
            }
        }

        // --- HÀM LOGIC PHÁN ĐỊNH ---
        private void AnalyzeResult(int foundCount, int targetCount)
        {
            int diff = foundCount - targetCount;

            if (diff == 0)
            {
                ResultDecision = "OK";
                ResultDetail = $"Đủ {targetCount} balls";
                ResultColor = Brushes.LimeGreen;
            }
            else
            {
                ResultDecision = "NG";
                ResultColor = Brushes.Red;
                if (diff < 0) ResultDetail = $"MISSING (Thiếu {Math.Abs(diff)})";
                else ResultDetail = $"EXTRA (Dư {diff})";
            }

            // Cập nhật text status nhỏ ở dưới
            TeachStatus = $"Found: {foundCount}/{targetCount} | {ResultDecision}";
        }

        [RelayCommand]
        private void AutoDetectBalls()
        {
            // 1. Kiểm tra an toàn
            if (SelectedFile == null)
            {
                MessageBox.Show("Vui lòng chọn một file ảnh trong danh sách bên trái trước khi Teach.");
                return;
            }

            // Cập nhật recipe từ UI
            _tempRecipe.MinBallAreaPx = MinArea;
            _tempRecipe.MaxBallAreaPx = MaxArea;
            _tempRecipe.MinCircularity = MinCircularity;
            _tempRecipe.UseAdaptiveThreshold = UseAdaptive;
            _tempRecipe.AutoThreshold = AdaptiveParamC;
            _tempRecipe.FixedThreshold = FixedThresholdVal;
            _tempRecipe.MorphKernelSize = MorphKernel;
            _tempRecipe.BallRadiusPx = TeachRadius;

            // Cập nhật ROI
            _tempRecipe.RoiX = (int)RoiLeft;
            _tempRecipe.RoiY = (int)RoiTop;
            _tempRecipe.RoiWidth = (int)RoiWidth;
            _tempRecipe.RoiHeight = (int)RoiHeight;

            try
            {
                // 1. Tìm vị trí các ball
                var result = _engine.AutoDetectAllBallsOpenCV(SelectedFile.FullPath, _tempRecipe);

                // 2. Ép kích thước hiển thị theo ý người dùng (TeachDiameter)
                double userDiameter = TeachDiameter;
                if (userDiameter <= 0) userDiameter = TeachRadius * 2;

                Application.Current.Dispatcher.Invoke(() => {
                    TeachOverlayShapes.Clear();
                    foreach (var p in result)
                    {
                        p.Diameter = userDiameter;
                        TeachOverlayShapes.Add(p);
                    }
                });
                TargetCount = result.Count;

                // Gọi hàm phân tích để cập nhật màu sắc (Xanh/Đỏ) và text thông báo
                AnalyzeResult(result.Count, TargetCount);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi Detect: {ex.Message}");
            }
        }

        // --- 1. LOAD PROGRAM & RUN BATCH ---
        [RelayCommand]
        private async Task LoadProgram()
        {
            // Mở hộp thoại chọn file .json trong thư mục Recipes
            var dialog = new OpenFileDialog
            {
                Filter = "Recipe Files (*.json)|*.json",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes")
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // 1. Load Recipe từ file đã chọn
                    string json = File.ReadAllText(dialog.FileName);
                    var loadedRecipe = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json);

                    if (loadedRecipe != null)
                    {
                        _activeManualRecipe = loadedRecipe;
                        ActiveRecipeName = loadedRecipe.RecipeId; // Hiển thị tên lên UI

                        // 2. Tự động áp dụng cho TOÀN BỘ ảnh trong list
                        await RunBatchInspection();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi load program: {ex.Message}");
                }
            }
        }

        // Trong MainViewModel.cs

        // Trong MainViewModel.cs

        private async Task RunBatchInspection()
        {
            if (_activeManualRecipe == null || PendingFiles.Count == 0) return;

            TeachStatus = "Đang chạy kiểm tra hàng loạt (Fixed Grid Check)...";

            await Task.Run(() =>
            {
                foreach (var item in PendingFiles)
                {
                    try
                    {
                        // [FIX LỖI 2] Thay vì AutoDetectAllBallsOpenCV (Tìm mới)
                        // Hãy gọi hàm InspectFixedGridWithAlignment (Kiểm tra theo tọa độ cũ)
                        var resultShapes = _engine.InspectFixedGridWithAlignment(item.FullPath, _activeManualRecipe);

                        // Tính toán kết quả dựa trên trạng thái OK/NG trả về
                        int ngCount = resultShapes.Count(s => s.State == "NG");

                        item.AutoDecision = ngCount == 0 ? "OK" : "NG";

                        if (ngCount > 0)
                            item.AutoDefectType = $"MISSING/VOID ({ngCount})";
                        else
                            item.AutoDefectType = "PASS";

                        item.AutoSummary = $"{item.AutoDecision} {item.AutoDefectType}";
                    }
                    catch (Exception ex)
                    {
                        item.AutoDecision = "ERR";
                        item.AutoSummary = "Error";
                    }
                }
            });

            TeachStatus = "Đã kiểm tra xong toàn bộ danh sách!";
        }

        // --- 2. CONFIRM NG (FILTER) ---
        [RelayCommand]
        private void FilterNg()
        {
            // Cài đặt bộ lọc cho View
            PendingFilesView.Filter = (obj) =>
            {
                if (obj is PendingFileItem item)
                {
                    // Chỉ hiện những file có kết quả là NG
                    return item.AutoDecision == "NG";
                }
                return false;
            };

            TeachStatus = "Đang hiển thị danh sách NG.";
        }

        [RelayCommand]
        private void ClearFilter()
        {
            // Xóa bộ lọc -> Hiện tất cả
            PendingFilesView.Filter = null;
            TeachStatus = "Hiển thị tất cả.";
        }

        // --- LỆNH BẬT/TẮT CHẾ ĐỘ VẼ ---
        [RelayCommand]
        private void ToggleDrawMode()
        {
            IsDrawMode = !IsDrawMode;
            TeachStatus = IsDrawMode ? "Chế độ vẽ ROI: Hãy kéo chuột trên ảnh" : "Đã tắt chế độ vẽ";
        }

        [RelayCommand]
        private void ClearRoi()
        {
            // Xóa ROI -> Kiểm tra toàn bộ ảnh
            RoiVisibility = Visibility.Collapsed;
            RoiWidth = 0;
            RoiHeight = 0;

            // Reset Recipe
            _tempRecipe.RoiWidth = 0;

            TeachStatus = "Đã xóa ROI. Sẽ kiểm tra toàn bộ ảnh.";
        }

        // --- 3. THÊM LỆNH LOAD RECIPE VÀO MÀN HÌNH TEACH ---
        [RelayCommand]
        private void LoadRecipeToTeach()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Recipe Files (*.json)|*.json",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes"),
                Title = "Chọn Recipe cũ để chỉnh sửa"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dialog.FileName);
                    var loadedRecipe = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json);

                    if (loadedRecipe != null)
                    {
                        // Map ngược dữ liệu từ Recipe vào các biến UI (ViewModel)
                        NewRecipeName = loadedRecipe.RecipeId; // Điền sẵn tên cũ

                        // Shape Filter
                        MinArea = loadedRecipe.MinBallAreaPx;
                        MaxArea = loadedRecipe.MaxBallAreaPx;
                        MinCircularity = loadedRecipe.MinCircularity;

                        // Threshold
                        UseAdaptive = loadedRecipe.UseAdaptiveThreshold;
                        AdaptiveParamC = (int)loadedRecipe.AutoThreshold;
                        FixedThresholdVal = loadedRecipe.FixedThreshold;

                        // Advanced
                        MorphKernel = loadedRecipe.MorphKernelSize;
                        TeachDiameter = loadedRecipe.BallRadiusPx * 2; // Tính lại đường kính
                        TargetCount = loadedRecipe.TargetBallCount;

                        // ROI (Vùng quan tâm)
                        if (loadedRecipe.HasRoi())
                        {
                            RoiLeft = loadedRecipe.RoiX;
                            RoiTop = loadedRecipe.RoiY;
                            RoiWidth = loadedRecipe.RoiWidth;
                            RoiHeight = loadedRecipe.RoiHeight;
                            RoiVisibility = Visibility.Visible;
                            IsDrawMode = false;
                        }
                        else
                        {
                            RoiVisibility = Visibility.Collapsed;
                            RoiWidth = 0;
                            RoiHeight = 0;
                        }

                        MessageBox.Show($"Đã load thông số từ Recipe: {loadedRecipe.RecipeId}.\nNhấn 'APPLY SETTINGS' để xem kết quả.", "Load thành công");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi đọc file Recipe: {ex.Message}");
                }
            }
        }

        // --- CÁC HÀM XỬ LÝ CHUỘT (GỌI TỪ VIEW) ---
        public void StartDrawing(Point p)
        {
            if (!IsDrawMode) return;

            _startPoint = p;
            RoiLeft = p.X;
            RoiTop = p.Y;
            RoiWidth = 0;
            RoiHeight = 0;
            RoiVisibility = Visibility.Visible;
        }

        public void UpdateDrawing(Point p)
        {
            if (!IsDrawMode || RoiVisibility != Visibility.Visible) return;

            // Tính toán hình chữ nhật từ điểm đầu và điểm hiện tại
            var x = Math.Min(p.X, _startPoint.X);
            var y = Math.Min(p.Y, _startPoint.Y);
            var w = Math.Abs(p.X - _startPoint.X);
            var h = Math.Abs(p.Y - _startPoint.Y);

            RoiLeft = x;
            RoiTop = y;
            RoiWidth = w;
            RoiHeight = h;
        }

        public void EndDrawing()
        {
            if (!IsDrawMode) return;

            // Lưu vào Recipe tạm thời
            _tempRecipe.RoiX = (int)RoiLeft;
            _tempRecipe.RoiY = (int)RoiTop;
            _tempRecipe.RoiWidth = (int)RoiWidth;
            _tempRecipe.RoiHeight = (int)RoiHeight;

            IsDrawMode = false; // Tự động tắt vẽ sau khi vẽ xong
            TeachStatus = $"Đã đặt vùng kiểm tra: {RoiWidth:F0}x{RoiHeight:F0}";
        }

        // Khi người dùng thay đổi đường kính -> Tự động tính Radius và Scan lại
        partial void OnTeachDiameterChanged(double value)
        {
            TeachRadius = (int)(value / 2); // Cập nhật bán kính
            if (TeachRadius < 1) TeachRadius = 1;
            //AutoDetectBalls(); // Scan lại ngay lập tức để thấy vòng tròn to/nhỏ thay đổi
        }

        // Khi người dùng nhập tay số lượng Target Count -> Cập nhật lại trạng thái OK/NG ngay
        partial void OnTargetCountChanged(int value)
        {
            // So sánh số lượng hiện có trên màn hình Teach với số mới nhập
            AnalyzeResult(TeachOverlayShapes.Count, value);
        }
    }
}