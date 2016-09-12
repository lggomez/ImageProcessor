﻿namespace ImageProcessorCore.Benchmarks
{
    using System.Drawing;
    using System.Drawing.Drawing2D;

    using BenchmarkDotNet.Attributes;
    using CoreSize = ImageProcessorCore.Size;
    using CoreImage = ImageProcessorCore.Image;

    public class Crop
    {
        [Benchmark(Baseline = true, Description = "System.Drawing Crop")]
        public Size CropSystemDrawing()
        {
            using (Bitmap source = new Bitmap(800, 800))
            {
                using (Bitmap destination = new Bitmap(100, 100))
                {
                    using (Graphics graphics = Graphics.FromImage(destination))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.DrawImage(source, new Rectangle(0, 0, 100, 100), 0, 0, 100, 100, GraphicsUnit.Pixel);
                    }

                    return destination.Size;
                }
            }
        }

        [Benchmark(Description = "ImageProcessorCore Crop")]
        public CoreSize CropResizeCore()
        {
            CoreImage image = new CoreImage(800, 800);
            image.Crop(100, 100);
            return new CoreSize(image.Width, image.Height);
        }
    }
}
