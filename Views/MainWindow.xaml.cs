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
            var container = sender as Grid;
            var vm = this.DataContext as MainViewModel;

            if (vm != null)
            {
                // Nếu bạn muốn giữ khả năng click trên ảnh để xử lý gì đó trong tương lai:
                if (TeachImage != null && TeachImage.Source != null)
                {
                    Point p = e.GetPosition(TeachImage);
                    double pixelX = p.X * (TeachImage.Source.Width / TeachImage.ActualWidth);
                    double pixelY = p.Y * (TeachImage.Source.Height / TeachImage.ActualHeight);
                    Point actualPixel = new Point(pixelX, pixelY);
                }
            }

            // 2. Chế độ thường: Pan (Kéo thả) - GIỮ NGUYÊN ĐOẠN NÀY
            if (e.ChangedButton == MouseButton.Left)
            {
                var scrollViewer = FindParent<ScrollViewer>(container);
                if (scrollViewer != null)
                {
                    _start = e.GetPosition(scrollViewer);
                    _origin = new Point(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);
                    container.CaptureMouse();
                    container.Cursor = Cursors.Hand;
                }
            }
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            var container = sender as Grid;
            if (container != null && container.IsMouseCaptured)
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
            if (container != null)
            {
                container.ReleaseMouseCapture();
                container.Cursor = Cursors.Arrow;
            }
        }
        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (true)
            {
                // Đi ngược lên cây giao diện để tìm cha
                DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);

                // Nếu không tìm thấy (đã lên đỉnh) -> trả về null
                if (parentObject == null) return null;

                // Nếu tìm thấy cha đúng kiểu mong muốn -> trả về
                if (parentObject is T parent) return parent;

                // Tiếp tục tìm lên trên
                child = parentObject;
            }
        }
    }
}