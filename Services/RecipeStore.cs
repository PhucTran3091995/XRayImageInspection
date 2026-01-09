using System;
using System.IO;
using System.Text.Json;
using WpfXrayQA.Models; // Quan trọng: dùng đúng Namespace Models

namespace WpfXrayQA.Services
{
    public sealed class RecipeStore
    {
        private readonly object _lock = new();
        private readonly string _rootDir;

        public RecipeStore()
        {
            // Lưu ngay tại thư mục chạy chương trình để dễ tìm: .\Recipes
            _rootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");
            if (!Directory.Exists(_rootDir)) Directory.CreateDirectory(_rootDir);
        }

        public string GetRecipePath(string recipeId)
        {
            string safeName = Sanitize(recipeId);
            return Path.Combine(_rootDir, safeName + ".json");
        }

        public string GetTemplatePath(string recipeId)
        {
            string safeName = Sanitize(recipeId);
            return Path.Combine(_rootDir, safeName + "_Template.png");
        }

        public void Save(Recipe recipe)
        {
            if (string.IsNullOrWhiteSpace(recipe.RecipeId)) return;

            var path = GetRecipePath(recipe.RecipeId);
            var json = JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true });

            lock (_lock)
            {
                File.WriteAllText(path, json);
            }
        }

        public bool TryLoad(string recipeId, out Recipe? recipe)
        {
            recipe = null;
            if (string.IsNullOrWhiteSpace(recipeId)) return false;

            var path = GetRecipePath(recipeId);
            if (!File.Exists(path)) return false;

            try
            {
                lock (_lock)
                {
                    var json = File.ReadAllText(path);
                    recipe = JsonSerializer.Deserialize<Recipe>(json);
                    return recipe != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}