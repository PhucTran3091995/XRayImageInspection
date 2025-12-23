using System.Windows;

namespace WpfXrayQA
{
    public partial class App : Application
    {
        // Khi app khởi chạy, đảm bảo DB được tạo
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Khởi tạo DB nếu chưa có
            using (var db = new Services.AppDbContext())
            {
                db.Database.EnsureCreated();
            }
        }
    }
}