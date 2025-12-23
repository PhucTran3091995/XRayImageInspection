using System.Collections.Generic;

namespace WpfXrayQA.Models // <-- Đã sửa namespace
{
    public sealed class InspectionResult
    {
        public bool HasRecipe { get; set; } = true;

        public string Decision { get; set; } = "OK";   // OK / NG / NO_RECIPE / ERROR
        public string DefectType { get; set; } = "NA"; // Missing / Short / Both / NA

        public int MissingCount { get; set; }
        public int ShortCount { get; set; }

        // Thêm 2 trường này để phục vụ CSV Log chi tiết
        public List<int> MissingIndices { get; set; } = new List<int>();
        public List<string> ShortPairs { get; set; } = new List<string>();

        public string Summary => HasRecipe
            ? $"{Decision} {DefectType} (M:{MissingCount}, S:{ShortCount})"
            : "NO_RECIPE";
    }
}