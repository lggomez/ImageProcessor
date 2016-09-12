﻿// <copyright file="ImageExtensions.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageProcessorCore
{
    using System.IO;

    using Formats;

    /// <summary>
    /// Extension methods for the <see cref="Image{TColor, TPacked}"/> type.
    /// </summary>
    public static partial class ImageExtensions
    {
        /// <summary>
        /// Saves the image to the given stream with the bmp format.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="source">The image this method extends.</param>
        /// <param name="stream">The stream to save the image to.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if the stream is null.</exception>
        public static void SaveAsBmp<TColor, TPacked>(this Image<TColor, TPacked> source, Stream stream)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
            => new BmpEncoder().Encode(source, stream);

        /// <summary>
        /// Saves the image to the given stream with the png format.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="source">The image this method extends.</param>
        /// <param name="stream">The stream to save the image to.</param>
        /// <param name="quality">The quality to save the image to representing the number of colors. 
        /// Anything equal to 256 and below will cause the encoder to save the image in an indexed format.
        /// </param>
        /// <exception cref="System.ArgumentNullException">Thrown if the stream is null.</exception>
        public static void SaveAsPng<TColor, TPacked>(this Image<TColor, TPacked> source, Stream stream, int quality = int.MaxValue)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
            => new PngEncoder { Quality = quality }.Encode(source, stream);

        /// <summary>
        /// Saves the image to the given stream with the jpeg format.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="source">The image this method extends.</param>
        /// <param name="stream">The stream to save the image to.</param>
        /// <param name="quality">The quality to save the image to. Between 1 and 100.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if the stream is null.</exception>
        public static void SaveAsJpeg<TColor, TPacked>(this Image<TColor, TPacked> source, Stream stream, int quality = 75)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
            => new JpegEncoder { Quality = quality }.Encode(source, stream);

        /// <summary>
        /// Saves the image to the given stream with the gif format.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="source">The image this method extends.</param>
        /// <param name="stream">The stream to save the image to.</param>
        /// <param name="quality">The quality to save the image to representing the number of colors. Between 1 and 256.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if the stream is null.</exception>
        internal static void SaveAsGif<TColor, TPacked>(this Image<TColor, TPacked> source, Stream stream, int quality = 256)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
            => new GifEncoder { Quality = quality }.Encode(source, stream);
    }
}
