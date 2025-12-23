using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfXrayQA.Models;
using WpfXrayQA.Services;
using System.Windows.Media;

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
        [ObservableProperty] private string settingRootPaths = @"C:\FakeXRayData";
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
        private Recipe _tempRecipe = new();

        // Collections
        public ObservableCollection<PendingFileItem> PendingFiles { get; } = new();
        public ObservableCollection<OverlayShape> GridOverlayShapes { get; } = new();

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

        // --- 2. Image Loading (Hàm bị thiếu trước đó) ---
        partial void OnSelectedFileChanged(PendingFileItem? value)
        {
            if (value != null && File.Exists(value.FullPath))
            {
                LoadImageSafe(value.FullPath);
                MessageStatus = $"File: {value.FileName} | Auto: {value.AutoSummary}";
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
            // 1. Kiểm tra điều kiện lưu (Ví dụ: phải có ảnh hoặc Model Name)
            if (SelectedFile == null)
            {
                MessageBox.Show("Vui lòng chọn một file ảnh để xác định Model trước khi lưu Recipe.");
                return;
            }

            try
            {
                // 2. Đưa tất cả thông số từ UI (Sliders/TextBoxes) vào đối tượng Recipe
                _tempRecipe.RecipeId = SelectedFile.ModelName;
                _tempRecipe.Model = SelectedFile.ModelName;

                // Lưu các thông số lọc Blob mới
                _tempRecipe.MinBallAreaPx = MinArea;
                _tempRecipe.MaxBallAreaPx = MaxArea;
                _tempRecipe.MinCircularity = MinCircularity;

                // Lưu thông số Threshold
                _tempRecipe.UseAdaptiveThreshold = UseAdaptive;
                _tempRecipe.AutoThreshold = AdaptiveParamC;
                _tempRecipe.FixedThreshold = FixedThresholdVal;

                // Lưu thông số nâng cao
                _tempRecipe.MorphKernelSize = MorphKernel;
                _tempRecipe.TargetBallCount = TargetCount;

                // Lưu kích thước ảnh tham khảo
                if (CurrentImage != null)
                {
                    _tempRecipe.ImageWidth = (int)CurrentImage.PixelWidth;
                    _tempRecipe.ImageHeight = (int)CurrentImage.PixelHeight;
                }

                // 3. Thực hiện lưu xuống File/Database
                _recipeStore.Save(_tempRecipe);

                MessageBox.Show($"Đã lưu Recipe cho Model: {_tempRecipe.RecipeId}\n" +
                                $"Số lượng Ball tiêu chuẩn: {TargetCount}", "Thành công",
                                MessageBoxButton.OK, MessageBoxImage.Information);

                // 4. Dọn dẹp trạng thái UI
                TeachStatus = "Recipe Saved.";
                // Không cần Clear Grid để người dùng vẫn nhìn thấy kết quả vừa quét
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu Recipe: {ex.Message}", "Lỗi",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void RemoveOverlay(OverlayShape item)
        {
            if (item == null) return;

            if (GridOverlayShapes.Contains(item))
            {
                // 1. Xóa khỏi danh sách hiển thị
                GridOverlayShapes.Remove(item);

                // 2. Cập nhật lại trạng thái số lượng (TargetCount)
                int found = GridOverlayShapes.Count;
                int diff = found - TargetCount;
                string resultStr = diff == 0 ? "OK (Khớp)" : (diff < 0 ? $"THIẾU {Math.Abs(diff)}" : $"DƯ {diff}");

                TeachStatus = $"Tìm thấy: {found} / {TargetCount} blob. -> {resultStr}";
            }
        }

        [RelayCommand]
        private void AutoDetectBalls()
        {
            if (SelectedFile == null)
            {
                MessageBox.Show("Chưa chọn ảnh!");
                return;
            }

            // 1. Đẩy tham số từ UI vào Recipe
            _tempRecipe.MinBallAreaPx = MinArea;
            _tempRecipe.MaxBallAreaPx = MaxArea;
            _tempRecipe.MinCircularity = MinCircularity;

            _tempRecipe.UseAdaptiveThreshold = UseAdaptive;
            _tempRecipe.AutoThreshold = AdaptiveParamC;
            _tempRecipe.FixedThreshold = FixedThresholdVal;

            _tempRecipe.MorphKernelSize = MorphKernel;
            _tempRecipe.BallRadiusPx = 15; // Mặc định hoặc binding nếu cần

            try
            {
                // 2. Chạy thuật toán
                var points = _engine.AutoDetectAllBallsOpenCV(SelectedFile.FullPath, _tempRecipe);

                GridOverlayShapes.Clear();
                foreach (var p in points) GridOverlayShapes.Add(p);

                // 3. So sánh số lượng
                int found = points.Count;
                int diff = found - TargetCount;
                string resultStr = diff == 0 ? "OK (Khớp)" : (diff < 0 ? $"THIẾU {Math.Abs(diff)}" : $"DƯ {diff}");

                TeachStatus = $"Tìm thấy: {found} / {TargetCount} blob. -> {resultStr}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}");
            }
        }


    }
}