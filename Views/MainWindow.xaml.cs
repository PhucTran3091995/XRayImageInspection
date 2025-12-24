using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfXrayQA.ViewModels;

namespace WpfXrayQA.Views
{
    public partial class MainWindow : Window
    {
        private Point _origin;
        private Point _start;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = new MainViewModel();
        }

        private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Zoom Container (chứa cả Ảnh và Overlay)
            var container = sender as Grid;
            if (container == null) return;

            var st = (ScaleTransform)container.RenderTransform;
            double zoom = e.Delta > 0 ? 0.2 : -0.2;
            double newScale = st.ScaleX + zoom;

            if (newScale < 0.1) newScale = 0.1;
            if (newScale > 50) newScale = 50;

            st.ScaleX = newScale;
            st.ScaleY = newScale;
            e.Handled = true;
        }
        private void Image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var element = sender as DependencyObject;
            if (element == null) return;

            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            // Grid bao ngoài (container)
            var container = sender as IInputElement;

            // 1. NẾU ĐANG Ở CHẾ ĐỘ VẼ ROI
            if (vm.IsDrawMode && e.LeftButton == MouseButtonState.Pressed)
            {
                Point p = e.GetPosition(TeachImage); // Lấy tọa độ trên ảnh
                vm.StartDrawing(p);

                // [SỬA LỖI] Dùng container để Capture chuột thay vì TeachImage
                // Để khớp với điều kiện kiểm tra ở MouseMove
                if (container != null)
                {
                    container.CaptureMouse();
                }
                return;
            }

            // 2. Logic Pan (Kéo thả ảnh)
            if (e.ChangedButton == MouseButton.Left)
            {
                var scrollViewer = FindParent<ScrollViewer>(element);
                if (scrollViewer != null)
                {
                    _start = e.GetPosition(scrollViewer);
                    _origin = new Point(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);

                    if (container != null)
                    {
                        container.CaptureMouse();
                        if (sender is FrameworkElement fe) fe.Cursor = Cursors.Hand;
                    }
                }
            }
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            var container = sender as Grid;

            // Kiểm tra Capture an toàn hơn: Nếu chuột trái không nhấn thì không làm gì
            if (container == null || e.LeftButton != MouseButtonState.Pressed) return;

            var vm = this.DataContext as MainViewModel;
            if (vm == null) return;

            // TRƯỜNG HỢP 1: ĐANG TRONG CHẾ ĐỘ VẼ ROI
            if (vm.IsDrawMode)
            {
                Point currentPos = e.GetPosition(TeachImage);
                vm.UpdateDrawing(currentPos);
            }
            // TRƯỜNG HỢP 2: CHẾ ĐỘ DI CHUYỂN ẢNH (PAN)
            else if (container.IsMouseCaptured) // Chỉ Pan khi Grid thực sự giữ chuột
            {
                var scrollViewer = FindParent<ScrollViewer>(container);
                if (scrollViewer != null)
                {
                    Point current = e.GetPosition(scrollViewer);
                    Vector v = _start - current;
                    scrollViewer.ScrollToHorizontalOffset(_origin.X + v.X);
                    scrollViewer.ScrollToVerticalOffset(_origin.Y + v.Y);
                }
            }
        }

        private void Image_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var container = sender as Grid;
            if (container == null) return;

            var vm = this.DataContext as MainViewModel;

            // 1. KẾT THÚC VẼ ROI
            if (vm != null && vm.IsDrawMode)
            {
                vm.EndDrawing();
            }

            // 2. GIẢI PHÓNG CHUỘT (Cho cả Vẽ và Pan)
            container.ReleaseMouseCapture();
            container.Cursor = Cursors.Arrow;
        }
        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            // [FIX QUAN TRỌNG] Kiểm tra null ngay đầu vào để tránh crash
            if (child == null) return null;

            while (true)
            {
                try
                {
                    // VisualTreeHelper.GetParent sẽ ném lỗi nếu child không phải là Visual hoặc Visual3D
                    // Nên ta cần kiểm tra loại đối tượng hoặc bọc try-catch để an toàn tuyệt đối
                    if (!(child is System.Windows.Media.Visual || child is System.Windows.Media.Media3D.Visual3D))
                    {
                        // Nếu không phải Visual, thử dùng LogicalTree (dự phòng)
                        DependencyObject logicalParent = LogicalTreeHelper.GetParent(child);
                        if (logicalParent == null) return null;
                        child = logicalParent;
                        continue;
                    }

                    DependencyObject parentObject = VisualTreeHelper.GetParent(child);

                    // Nếu không tìm thấy (đã lên đỉnh) -> trả về null
                    if (parentObject == null) return null;

                    // Nếu tìm thấy cha đúng kiểu mong muốn -> trả về
                    if (parentObject is T parent) return parent;

                    // Tiếp tục tìm lên trên
                    child = parentObject;
                }
                catch
                {
                    // Nếu có bất kỳ lỗi gì khi leo cây giao diện, trả về null thay vì crash app
                    return null;
                }
            }
        }
    }
}