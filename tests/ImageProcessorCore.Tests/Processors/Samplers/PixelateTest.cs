﻿// <copyright file="PixelateTest.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageProcessorCore.Tests
{
    using System.IO;

    using Xunit;

    public class PixelateTest : FileTestBase
    {
        public static readonly TheoryData<int> PixelateValues
        = new TheoryData<int>
        {
            4 ,
            8
        };

        [Theory]
        [MemberData(nameof(PixelateValues))]
        public void ImageShouldApplyPixelateFilter(int value)
        {
            const string path = "TestOutput/Pixelate";
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (string file in Files)
            {
                using (FileStream stream = File.OpenRead(file))
                {
                    string filename = Path.GetFileNameWithoutExtension(file) + "-" + value + Path.GetExtension(file);

                    Image image = new Image(stream);
                    using (FileStream output = File.OpenWrite($"{path}/{filename}"))
                    {
                        image.Pixelate(value)
                             .Save(output);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(PixelateValues))]
        public void ImageShouldApplyPixelateFilterInBox(int value)
        {
            const string path = "TestOutput/Pixelate";
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (string file in Files)
            {
                using (FileStream stream = File.OpenRead(file))
                {
                    string filename = Path.GetFileNameWithoutExtension(file) + "-" + value + "-InBox" + Path.GetExtension(file);

                    Image image = new Image(stream);
                    using (FileStream output = File.OpenWrite($"{path}/{filename}"))
                    {
                        image.Pixelate(value, new Rectangle(10, 10, image.Width / 2, image.Height / 2))
                             .Save(output);
                    }
                }
            }
        }
    }
}