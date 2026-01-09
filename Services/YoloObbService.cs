using System;
using System.Collections.Generic;
using System.IO; // Cần thêm namespace này để dùng Path
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using WpfXrayQA.Models;

namespace WpfXrayQA.Services
{
    public class YoloObbService : IDisposable
    {
        private InferenceSession _session;
        private readonly int _modelSize = 640;
        private readonly string _modelPath; // Khai báo nhưng không gán giá trị tại đây

        public YoloObbService()
        {
            // 1. Gán giá trị đường dẫn bên trong Constructor
            _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "best.onnx");

            // 2. Kiểm tra file có tồn tại không trước khi nạp
            if (!File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"Không tìm thấy file mô hình tại: {_modelPath}");
            }

            var options = new SessionOptions();

            // 3. Sử dụng đúng tên biến _modelPath (có dấu gạch dưới)
            _session = new InferenceSession(_modelPath, options);
        }

        public List<OverlayShape> DetectShorts(string imagePath)
        {
            var results = new List<OverlayShape>();

            using var src = Cv2.ImRead(imagePath);
            if (src.Empty()) return results;

            using var resized = new Mat();
            Cv2.Resize(src, resized, new Size(_modelSize, _modelSize));

            var inputTensor = ConvertImageToTensor(resized);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images", inputTensor)
            };

            using var output = _session.Run(inputs);
            var outputData = output.First().AsTensor<float>();

            results = ParseDetectOutput(outputData, src.Width, src.Height);

            return results;
        }

        private Tensor<float> ConvertImageToTensor(Mat img)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, _modelSize, _modelSize });
            for (int y = 0; y < _modelSize; y++)
            {
                for (int x = 0; x < _modelSize; x++)
                {
                    var color = img.At<Vec3b>(y, x);
                    // YOLO thường yêu cầu định dạng RGB, OpenCV mặc định là BGR
                    tensor[0, 0, y, x] = color.Item2 / 255.0f; // R
                    tensor[0, 1, y, x] = color.Item1 / 255.0f; // G
                    tensor[0, 2, y, x] = color.Item0 / 255.0f; // B
                }
            }
            return tensor;
        }

        private List<OverlayShape> ParseDetectOutput(Tensor<float> output, int imgW, int imgH)
        {
            var shapes = new List<OverlayShape>();
            float confThreshold = 0.5f;

            float scaleX = (float)imgW / _modelSize;
            float scaleY = (float)imgH / _modelSize;

            // Output của YOLO11m Detect nc=1 là [1, 5, 8400]
            // 0:x_center, 1:y_center, 2:width, 3:height, 4:score
            for (int i = 0; i < 8400; i++)
            {
                float score = output[0, 4, i];
                if (score > confThreshold)
                {
                    float x_center = output[0, 0, i] * scaleX;
                    float y_center = output[0, 1, i] * scaleY;
                    float w = output[0, 2, i] * scaleX;
                    float h = output[0, 3, i] * scaleY;

                    shapes.Add(new OverlayShape
                    {
                        X = x_center,
                        Y = y_center,
                        Width = w,
                        Height = h,
                        AngleDegree = 0, // YOLO Detect không có góc xoay
                        Diameter = Math.Max(w, h),
                        IsLine = false, // Chân ball hình tròn nên để false
                        State = "BALL",
                        TooltipInfo = $"AI Conf: {score:P0}"
                    });
                }
            }
            return shapes;
        }

        public void Dispose() => _session?.Dispose();
    }
}