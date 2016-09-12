﻿// <copyright file="ColorTests.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

using System.Numerics;

namespace ImageProcessorCore.Tests
{
    using System;
    using Xunit;

    /// <summary>
    /// Tests the <see cref="Color"/> struct.
    /// </summary>
    public class ColorTests
    {
        /// <summary>
        /// Tests the equality operators for equality.
        /// </summary>
        [Fact]
        public void AreEqual()
        {
            Color color1 = new Color(0, 0, 0);
            Color color2 = new Color(0, 0, 0, 1F);
            Color color3 = new Color("#000");
            Color color4 = new Color("#000000");
            Color color5 = new Color("#FF000000");

            Assert.Equal(color1, color2);
            Assert.Equal(color1, color3);
            Assert.Equal(color1, color4);
            Assert.Equal(color1, color5);
        }

        /// <summary>
        /// Tests the equality operators for inequality.
        /// </summary>
        [Fact]
        public void AreNotEqual()
        {
            Color color1 = new Color(255, 0, 0, 255);
            Color color2 = new Color(0, 0, 0, 255);
            Color color3 = new Color("#000");
            Color color4 = new Color("#000000");
            Color color5 = new Color("#FF000000");

            Assert.NotEqual(color1, color2);
            Assert.NotEqual(color1, color3);
            Assert.NotEqual(color1, color4);
            Assert.NotEqual(color1, color5);
        }

        /// <summary>
        /// Tests whether the color constructor correctly assign properties.
        /// </summary>
        [Fact]
        public void ConstructorAssignsProperties()
        {
            Color color1 = new Color(1, .1f, .133f, .864f);
            Assert.Equal(255, color1.R);
            Assert.Equal((byte)Math.Round(.1f * 255), color1.G);
            Assert.Equal((byte)Math.Round(.133f * 255), color1.B);
            Assert.Equal((byte)Math.Round(.864f * 255), color1.A);

            Color color2 = new Color(1, .1f, .133f);
            Assert.Equal(255, color2.R);
            Assert.Equal(Math.Round(.1f * 255), color2.G);
            Assert.Equal(Math.Round(.133f * 255), color2.B);
            Assert.Equal(255, color2.A);

            Color color3 = new Color("#FF0000");
            Assert.Equal(255, color3.R);
            Assert.Equal(0, color3.G);
            Assert.Equal(0, color3.B);
            Assert.Equal(255, color3.A);

            Color color4 = new Color(new Vector3(1, .1f, .133f));
            Assert.Equal(255, color4.R);
            Assert.Equal(Math.Round(.1f * 255), color4.G);
            Assert.Equal(Math.Round(.133f * 255), color4.B);
            Assert.Equal(255, color4.A);

            Color color5 = new Color(new Vector4(1, .1f, .133f, .5f));
            Assert.Equal(255, color5.R);
            Assert.Equal(Math.Round(.1f * 255), color5.G);
            Assert.Equal(Math.Round(.133f * 255), color5.B);
            Assert.Equal(Math.Round(.5f * 255), color5.A);
        }

        /// <summary>
        /// Tests to see that in the input hex matches that of the output.
        /// </summary>
        [Fact]
        public void ConvertHex()
        {
            const string First = "FF000000";
            Color color = Color.Black;
            string second = color.PackedValue.ToString("X");
            Assert.Equal(First, second);
        }
    }
}