using Microsoft.EntityFrameworkCore; // <-- Phải có dòng này
using Microsoft.EntityFrameworkCore.Infrastructure; // <-- Add this if not present
using System;
using System.Collections.Generic;
using System.Linq;
using WpfXrayQA.Models;

namespace WpfXrayQA.Services
{
    // Lớp này PHẢI kế thừa từ DbContext
    public class AppDbContext : DbContext
    {
        public DbSet<ReviewLog> ReviewLogs { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Cấu hình dùng SQLite
            optionsBuilder.UseSqlite("Data Source=xray_qa_log.db");
        }
    }

    public class DatabaseService
    {
        public bool IsReviewed(string fullPath, DateTime lastWrite, long size)
        {
            using var db = new AppDbContext();
            // Đảm bảo DB đã được tạo
            db.Database.EnsureCreated();

            return db.ReviewLogs.Any(x => x.FullPath == fullPath && x.LastWriteTimeUtc == lastWrite);
        }

        public void SaveLog(ReviewLog log)
        {
            using var db = new AppDbContext();
            db.Database.EnsureCreated(); // Đảm bảo bảng tồn tại trước khi lưu

            db.ReviewLogs.Add(log);
            db.SaveChanges(); // <-- Lỗi sẽ biến mất nếu class AppDbContext kế thừa DbContext đúng
        }
    }
}