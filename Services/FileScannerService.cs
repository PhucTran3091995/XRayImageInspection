using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WpfXrayQA.Services
{
    public class FileScannerService
    {
        // Chỉ lấy file kết thúc bằng _1_8.jpg
        private readonly Regex _filePattern = new Regex(@"^\d{14}_1_8\.jpg$", RegexOptions.Compiled);

        public async Task<List<string>> ScanRootAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var resultFiles = new List<string>();
                if (!Directory.Exists(rootPath)) return resultFiles;

                try
                {
                    // 1. Tìm folder ngày (Format 8 số), sắp xếp giảm dần để lấy mới nhất
                    var dateDir = Directory.GetDirectories(rootPath)
                                           .Select(d => new DirectoryInfo(d))
                                           .Where(d => Regex.IsMatch(d.Name, @"^\d{8}$"))
                                           .OrderByDescending(d => d.Name)
                                           .FirstOrDefault();

                    if (dateDir == null) return resultFiles;

                    // 2. Quét các Model bên trong ngày mới nhất
                    var modelDirs = Directory.GetDirectories(dateDir.FullName);
                    foreach (var modelDir in modelDirs)
                    {
                        // 3. Chỉ vào folder GD
                        string gdPath = Path.Combine(modelDir, "GD");
                        if (Directory.Exists(gdPath))
                        {
                            // 4. Lọc file *_1_8.jpg
                            var files = Directory.GetFiles(gdPath, "*_1_8.jpg");
                            foreach (var file in files)
                            {
                                if (_filePattern.IsMatch(Path.GetFileName(file)))
                                {
                                    resultFiles.Add(file);
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Bỏ qua lỗi access denied để không crash app
                }

                return resultFiles;
            });
        }
    }
}