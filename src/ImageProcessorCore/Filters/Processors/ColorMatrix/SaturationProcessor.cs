﻿// <copyright file="SaturationProcessor.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageProcessorCore.Processors
{
    using System.Numerics;

    /// <summary>
    /// An <see cref="IImageProcessor{TColor, TPacked}"/> to change the saturation of an <see cref="Image{TColor, TPacked}"/>.
    /// </summary>
    /// <typeparam name="TColor">The pixel format.</typeparam>
    /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
    public class SaturationProcessor<TColor, TPacked> : ColorMatrixFilter<TColor, TPacked>
        where TColor : IPackedVector<TPacked>
        where TPacked : struct
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SaturationProcessor{TColor, TPacked}"/> class.
        /// </summary>
        /// <param name="saturation">The new saturation of the image. Must be between -100 and 100.</param>
        /// <exception cref="System.ArgumentException">
        /// <paramref name="saturation"/> is less than -100 or is greater than 100.
        /// </exception>
        public SaturationProcessor(int saturation)
        {
            Guard.MustBeBetweenOrEqualTo(saturation, -100, 100, nameof(saturation));
            float saturationFactor = saturation / 100f;

            // Stop at -1 to prevent inversion.
            saturationFactor++;

            // The matrix is set up to "shear" the colour space using the following set of values.
            // Note that each colour component has an effective luminance which contributes to the
            // overall brightness of the pixel.
            // See http://graficaobscura.com/matrix/index.html
            float saturationComplement = 1.0f - saturationFactor;
            float saturationComplementR = 0.3086f * saturationComplement;
            float saturationComplementG = 0.6094f * saturationComplement;
            float saturationComplementB = 0.0820f * saturationComplement;

            Matrix4x4 matrix4X4 = new Matrix4x4()
            {
                M11 = saturationComplementR + saturationFactor,
                M12 = saturationComplementR,
                M13 = saturationComplementR,
                M21 = saturationComplementG,
                M22 = saturationComplementG + saturationFactor,
                M23 = saturationComplementG,
                M31 = saturationComplementB,
                M32 = saturationComplementB,
                M33 = saturationComplementB + saturationFactor,
            };

            this.Matrix = matrix4X4;
        }

        /// <inheritdoc/>
        public override Matrix4x4 Matrix { get; }
    }
}
