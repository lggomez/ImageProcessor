﻿// <copyright file="JpegDecoderCore.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>
namespace ImageProcessorCore.Formats
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// Performs the jpeg decoding operation.
    /// </summary>
    internal class JpegDecoderCore
    {
        /// <summary>
        /// The maximum (inclusive) number of bits in a Huffman code.
        /// </summary>
        private const int MaxCodeLength = 16;

        /// <summary>
        /// The maximum (inclusive) number of codes in a Huffman tree.
        /// </summary>
        private const int MaxNCodes = 256;

        /// <summary>
        /// The log-2 size of the Huffman decoder's look-up table.
        /// </summary>
        private const int LutSize = 8;

        private const int maxComponents = 4;

        private const int maxTc = 1;

        private const int maxTh = 3;

        private const int maxTq = 3;

        private const int dcTable = 0;

        private const int acTable = 1;

        /// <summary>
        /// <see href="http://www.sno.phy.queensu.ca/~phil/exiftool/TagNames/JPEG.html#Adobe"/>
        /// </summary>
        private const int adobeTransformUnknown = 0;

        private const int adobeTransformYCbCr = 1;

        private const int adobeTransformYCbCrK = 2;

        /// <summary>
        /// Unzig maps from the zig-zag ordering to the natural ordering. For example,
        /// unzig[3] is the column and row of the fourth element in zig-zag order. The
        /// value is 16, which means first column (16%8 == 0) and third row (16/8 == 2).
        /// </summary>
        private static readonly int[] Unzig =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26,
            33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57,
            50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31,
            39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        /// <summary>
        /// The image width
        /// </summary>
        private int widthG;

        /// <summary>
        /// The image height
        /// </summary>
        private int heightG;

        /// <summary>
        /// The number of color components within the image.
        /// </summary>
        private int componentCount;

        /// <summary>
        /// A grayscale image to decode to.
        /// </summary>
        private GrayImage grayImage;

        /// <summary>
        /// The full color image to decode to.
        /// </summary>
        private YCbCrImage ycbcrImage;

        private Stream inputStream;

        private bits_class bits;

        private bytes_class bytes;

        private byte[] blackPix;

        private int blackStride;

        /// <summary>
        /// The restart interval
        /// </summary>
        private int restartInterval;

        /// <summary>
        /// Whether the image is interlaced (progressive)
        /// </summary>
        private bool isProgressive;

        /// <summary>
        /// Whether the image has a Jfif header
        /// </summary>
        private bool isJfif;

        private bool adobeTransformValid;

        private byte adobeTransform;

        /// <summary>
        /// End-of-Band run, specified in section G.1.2.2.
        /// </summary>
        private ushort eobRun;

        private Component[] comp;

        /// <summary>
        /// Saved state between progressive-mode scans.
        /// </summary>
        private Block[][] progCoeffs;

        private Huffman[,] huff;

        private Block[] quant; // Quantization tables, in zig-zag order.

        private byte[] tmp;

        /// <summary>
        /// The horizontal resolution. Calculated if the image has a JFIF header.
        /// </summary>
        private short horizontalResolution;

        /// <summary>
        /// The vertical resolution. Calculated if the image has a JFIF header.
        /// </summary>
        private short verticalResolution;

        /// <summary>
        /// Initializes a new instance of the <see cref="JpegDecoderCore"/> class.
        /// </summary>
        public JpegDecoderCore()
        {
            this.huff = new Huffman[maxTc + 1, maxTh + 1];
            this.quant = new Block[maxTq + 1];
            this.tmp = new byte[2 * Block.BlockSize];
            this.comp = new Component[maxComponents];
            this.progCoeffs = new Block[maxComponents][];
            this.bits = new bits_class();
            this.bytes = new bytes_class();

            for (int i = 0; i < maxTc + 1; i++)
            {
                for (int j = 0; j < maxTh + 1; j++)
                {
                    this.huff[i, j] = new Huffman();
                }
            }

            for (int i = 0; i < this.quant.Length; i++)
            {
                this.quant[i] = new Block();
            }

            for (int i = 0; i < this.comp.Length; i++)
            {
                this.comp[i] = new Component();
            }
        }

        // ensureNBits reads bytes from the byte buffer to ensure that bits.n is at
        // least n. For best performance (avoiding function calls inside hot loops),
        // the caller is the one responsible for first checking that bits.n < n.
        private void EnsureNBits(int n)
        {
            while (true)
            {
                byte c = this.ReadByteStuffedByte();
                this.bits.a = (this.bits.a << 8) | (uint)c;
                this.bits.n += 8;
                if (this.bits.m == 0) this.bits.m = 1 << 7;
                else this.bits.m <<= 8;
                if (this.bits.n >= n) break;
            }
        }

        // receiveExtend is the composition of RECEIVE and EXTEND, specified in section
        // F.2.2.1.
        private int ReceiveExtend(byte t)
        {
            if (this.bits.n < t) this.EnsureNBits(t);

            this.bits.n -= t;
            this.bits.m >>= t;
            int s = 1 << t;
            int x = (int)((this.bits.a >> this.bits.n) & (s - 1));
            if (x < (s >> 1)) x += ((-1) << t) + 1;
            return x;
        }

        /// <summary>
        /// Processes a Define Huffman Table marker, and initializes a huffman
        /// struct from its contents. Specified in section B.2.4.2.
        /// </summary>
        /// <param name="n"></param>
        private void ProcessDHT(int n)
        {
            while (n > 0)
            {
                if (n < 17)
                {
                    throw new ImageFormatException("DHT has wrong length");
                }

                this.ReadFull(this.tmp, 0, 17);

                int tc = this.tmp[0] >> 4;
                if (tc > maxTc)
                {
                    throw new ImageFormatException("bad Tc value");
                }

                int th = this.tmp[0] & 0x0f;
                if (th > maxTh || !this.isProgressive && th > 1)
                {
                    throw new ImageFormatException("bad Th value");
                }

                Huffman h = this.huff[tc, th];

                // Read nCodes and h.vals (and derive h.nCodes).
                // nCodes[i] is the number of codes with code length i.
                // h.nCodes is the total number of codes.
                h.nCodes = 0;

                int[] ncodes = new int[MaxCodeLength];
                for (int i = 0; i < ncodes.Length; i++)
                {
                    ncodes[i] = this.tmp[i + 1];
                    h.nCodes += ncodes[i];
                }

                if (h.nCodes == 0) throw new ImageFormatException("Huffman table has zero length");
                if (h.nCodes > MaxNCodes) throw new ImageFormatException("Huffman table has excessive length");

                n -= h.nCodes + 17;
                if (n < 0) throw new ImageFormatException("DHT has wrong length");

                this.ReadFull(h.vals, 0, h.nCodes);

                // Derive the look-up table.
                for (int i = 0; i < h.lut.Length; i++) h.lut[i] = 0;

                uint x = 0, code = 0;

                for (int i = 0; i < LutSize; i++)
                {
                    code <<= 1;

                    for (int j = 0; j < ncodes[i]; j++)
                    {
                        // The codeLength is 1+i, so shift code by 8-(1+i) to
                        // calculate the high bits for every 8-bit sequence
                        // whose codeLength's high bits matches code.
                        // The high 8 bits of lutValue are the encoded value.
                        // The low 8 bits are 1 plus the codeLength.
                        byte base2 = (byte)(code << (7 - i));
                        ushort lutValue = (ushort)(((ushort)h.vals[x] << 8) | (2 + i));
                        for (int k = 0; k < 1 << (7 - i); k++) h.lut[base2 | k] = lutValue;
                        code++;
                        x++;
                    }
                }

                // Derive minCodes, maxCodes, and valsIndices.
                int c = 0, index = 0;
                for (int i = 0; i < ncodes.Length; i++)
                {
                    int nc = ncodes[i];
                    if (nc == 0)
                    {
                        h.minCodes[i] = -1;
                        h.maxCodes[i] = -1;
                        h.valsIndices[i] = -1;
                    }
                    else
                    {
                        h.minCodes[i] = c;
                        h.maxCodes[i] = c + nc - 1;
                        h.valsIndices[i] = index;
                        c += nc;
                        index += nc;
                    }

                    c <<= 1;
                }
            }
        }

        // decodeHuffman returns the next Huffman-coded value from the bit-stream,
        // decoded according to h.
        private byte decodeHuffman(Huffman h)
        {
            if (h.nCodes == 0) throw new ImageFormatException("uninitialized Huffman table");

            if (this.bits.n < 8)
            {
                try
                {
                    this.EnsureNBits(8);
                }
                catch (MissingFF00Exception)
                {
                    if (this.bytes.nUnreadable != 0) this.unreadByteStuffedByte();
                    goto slowPath;
                }
                catch (ShortHuffmanDataException)
                {
                    if (this.bytes.nUnreadable != 0) this.unreadByteStuffedByte();
                    goto slowPath;
                }
            }

            ushort v = h.lut[(this.bits.a >> (this.bits.n - LutSize)) & 0xff];
            if (v != 0)
            {
                byte n = (byte)((v & 0xff) - 1);
                this.bits.n -= n;
                this.bits.m >>= n;
                return (byte)(v >> 8);
            }

            slowPath:
            int code = 0;
            for (int i = 0; i < MaxCodeLength; i++)
            {
                if (this.bits.n == 0) this.EnsureNBits(1);
                if ((this.bits.a & this.bits.m) != 0) code |= 1;
                this.bits.n--;
                this.bits.m >>= 1;
                if (code <= h.maxCodes[i]) return h.vals[h.valsIndices[i] + code - h.minCodes[i]];
                code <<= 1;
            }

            throw new ImageFormatException("bad Huffman code");
        }

        private bool decodeBit()
        {
            if (this.bits.n == 0) this.EnsureNBits(1);

            bool ret = (this.bits.a & this.bits.m) != 0;
            this.bits.n--;
            this.bits.m >>= 1;
            return ret;
        }

        private uint decodeBits(int n)
        {
            if (this.bits.n < n) this.EnsureNBits(n);

            uint ret = this.bits.a >> (this.bits.n - n);
            ret = (uint)(ret & ((1 << n) - 1));
            this.bits.n -= n;
            this.bits.m >>= n;
            return ret;
        }

        // fill fills up the bytes.buf buffer from the underlying io.Reader. It
        // should only be called when there are no unread bytes in bytes.
        private void fill()
        {
            if (this.bytes.i != this.bytes.j) throw new ImageFormatException("jpeg: fill called when unread bytes exist");

            // Move the last 2 bytes to the start of the buffer, in case we need
            // to call unreadByteStuffedByte.
            if (this.bytes.j > 2)
            {
                this.bytes.buf[0] = this.bytes.buf[this.bytes.j - 2];
                this.bytes.buf[1] = this.bytes.buf[this.bytes.j - 1];
                this.bytes.i = 2;
                this.bytes.j = 2;
            }

            // Fill in the rest of the buffer.
            int n = this.inputStream.Read(this.bytes.buf, this.bytes.j, this.bytes.buf.Length - this.bytes.j);
            if (n == 0) throw new EOFException();
            this.bytes.j += n;
        }

        // unreadByteStuffedByte undoes the most recent readByteStuffedByte call,
        // giving a byte of data back from bits to bytes. The Huffman look-up table
        // requires at least 8 bits for look-up, which means that Huffman decoding can
        // sometimes overshoot and read one or two too many bytes. Two-byte overshoot
        // can happen when expecting to read a 0xff 0x00 byte-stuffed byte.
        private void unreadByteStuffedByte()
        {
            this.bytes.i -= this.bytes.nUnreadable;
            this.bytes.nUnreadable = 0;
            if (this.bits.n >= 8)
            {
                this.bits.a >>= 8;
                this.bits.n -= 8;
                this.bits.m >>= 8;
            }
        }

        // readByte returns the next byte, whether buffered or not buffere It does
        // not care about byte stuffing.
        private byte ReadByte()
        {
            while (this.bytes.i == this.bytes.j) this.fill();
            byte x = this.bytes.buf[this.bytes.i];
            this.bytes.i++;
            this.bytes.nUnreadable = 0;
            return x;
        }

        /// <summary>
        /// ReadByteStuffedByte is like ReadByte but is for byte-stuffed Huffman data.
        /// </summary>
        /// <returns>The <see cref="byte"/></returns>
        private byte ReadByteStuffedByte()
        {
            byte x;

            // Take the fast path if bytes.buf contains at least two bytes.
            if (this.bytes.i + 2 <= this.bytes.j)
            {
                x = this.bytes.buf[this.bytes.i];
                this.bytes.i++;
                this.bytes.nUnreadable = 1;
                if (x != JpegConstants.Markers.XFF)
                {
                    return x;
                }

                if (this.bytes.buf[this.bytes.i] != 0x00)
                {
                    throw new MissingFF00Exception();
                }

                this.bytes.i++;
                this.bytes.nUnreadable = 2;
                return 0xff;
            }

            this.bytes.nUnreadable = 0;

            x = this.ReadByte();
            this.bytes.nUnreadable = 1;
            if (x != 0xff) return x;
            x = this.ReadByte();
            this.bytes.nUnreadable = 2;
            if (x != 0x00) throw new MissingFF00Exception();
            return 0xff;
        }

        // readFull reads exactly len(p) bytes into p. It does not care about byte
        // stuffing.
        private void ReadFull(byte[] data, int offset, int len)
        {
            // Unread the overshot bytes, if any.
            if (this.bytes.nUnreadable != 0)
            {
                if (this.bits.n >= 8) this.unreadByteStuffedByte();
                this.bytes.nUnreadable = 0;
            }

            while (len > 0)
            {
                if (this.bytes.j - this.bytes.i >= len)
                {
                    Array.Copy(this.bytes.buf, this.bytes.i, data, offset, len);
                    this.bytes.i += len;
                    len -= len;
                }
                else
                {
                    Array.Copy(this.bytes.buf, this.bytes.i, data, offset, this.bytes.j - this.bytes.i);
                    offset += this.bytes.j - this.bytes.i;
                    len -= this.bytes.j - this.bytes.i;
                    this.bytes.i += this.bytes.j - this.bytes.i;

                    this.fill();
                }
            }
        }

        // ignore ignores the next n bytes.
        private void ignore(int n)
        {
            // Unread the overshot bytes, if any.
            if (this.bytes.nUnreadable != 0)
            {
                if (this.bits.n >= 8) this.unreadByteStuffedByte();
                this.bytes.nUnreadable = 0;
            }

            while (true)
            {
                int m = this.bytes.j - this.bytes.i;
                if (m > n) m = n;
                this.bytes.i += m;
                n -= m;
                if (n == 0) break;
                else this.fill();
            }
        }

        // Specified in section B.2.2.
        private void ProcessSOF(int n)
        {
            if (this.componentCount != 0)
            {
                throw new ImageFormatException("multiple SOF markers");
            }

            switch (n)
            {
                case 6 + (3 * 1): // Grayscale image.
                    this.componentCount = 1;
                    break;
                case 6 + (3 * 3): // YCbCr or RGB image.
                    this.componentCount = 3;
                    break;
                case 6 + (3 * 4): // YCbCrK or CMYK image.
                    this.componentCount = 4;
                    break;
                default:
                    throw new ImageFormatException("Incorrect number of components");
            }

            this.ReadFull(this.tmp, 0, n);

            // We only support 8-bit precision.
            if (this.tmp[0] != 8)
            {
                throw new ImageFormatException("Only 8-Bit precision supported.");
            }

            this.heightG = (this.tmp[1] << 8) + this.tmp[2];
            this.widthG = (this.tmp[3] << 8) + this.tmp[4];
            if (this.tmp[5] != this.componentCount)
            {
                throw new ImageFormatException("SOF has wrong length");
            }

            for (int i = 0; i < this.componentCount; i++)
            {
                this.comp[i].Identifier = this.tmp[6 + (3 * i)];

                // Section B.2.2 states that "the value of C_i shall be different from
                // the values of C_1 through C_(i-1)".
                for (int j = 0; j < i; j++)
                {
                    if (this.comp[i].Identifier == this.comp[j].Identifier)
                    {
                        throw new ImageFormatException("Repeated component identifier");
                    }
                }

                this.comp[i].Selector = this.tmp[8 + (3 * i)];
                if (this.comp[i].Selector > maxTq)
                {
                    throw new ImageFormatException("Bad Tq value");
                }

                byte hv = this.tmp[7 + (3 * i)];
                int h = hv >> 4;
                int v = hv & 0x0f;
                if (h < 1 || 4 < h || v < 1 || 4 < v)
                {
                    throw new ImageFormatException("Unsupported Luma/chroma subsampling ratio");
                }

                if (h == 3 || v == 3)
                {
                    throw new ImageFormatException("Lnsupported subsampling ratio");
                }

                switch (this.componentCount)
                {
                    case 1:

                        // If a JPEG image has only one component, section A.2 says "this data
                        // is non-interleaved by definition" and section A.2.2 says "[in this
                        // case...] the order of data units within a scan shall be left-to-right
                        // and top-to-bottom... regardless of the values of H_1 and V_1". Section
                        // 4.8.2 also says "[for non-interleaved data], the MCU is defined to be
                        // one data unit". Similarly, section A.1.1 explains that it is the ratio
                        // of H_i to max_j(H_j) that matters, and similarly for V. For grayscale
                        // images, H_1 is the maximum H_j for all components j, so that ratio is
                        // always 1. The component's (h, v) is effectively always (1, 1): even if
                        // the nominal (h, v) is (2, 1), a 20x5 image is encoded in three 8x8
                        // MCUs, not two 16x8 MCUs.
                        h = 1;
                        v = 1;
                        break;

                    case 3:

                        // For YCbCr images, we only support 4:4:4, 4:4:0, 4:2:2, 4:2:0,
                        // 4:1:1 or 4:1:0 chroma subsampling ratios. This implies that the
                        // (h, v) values for the Y component are either (1, 1), (1, 2),
                        // (2, 1), (2, 2), (4, 1) or (4, 2), and the Y component's values
                        // must be a multiple of the Cb and Cr component's values. We also
                        // assume that the two chroma components have the same subsampling
                        // ratio.
                        switch (i)
                        {
                            case 0:
                                {
                                    // Y.
                                    // We have already verified, above, that h and v are both
                                    // either 1, 2 or 4, so invalid (h, v) combinations are those
                                    // with v == 4.
                                    if (v == 4)
                                    {
                                        throw new ImageFormatException("unsupported subsampling ratio");
                                    }

                                    break;
                                }

                            case 1:
                                {
                                    // Cb.
                                    if (this.comp[0].HorizontalFactor % h != 0 || this.comp[0].VerticalFactor % v != 0)
                                    {
                                        throw new ImageFormatException("unsupported subsampling ratio");
                                    }

                                    break;
                                }

                            case 2:
                                {
                                    // Cr.
                                    if (this.comp[1].HorizontalFactor != h || this.comp[1].VerticalFactor != v)
                                    {
                                        throw new ImageFormatException("unsupported subsampling ratio");
                                    }

                                    break;
                                }
                        }

                        break;

                    case 4:

                        // For 4-component images (either CMYK or YCbCrK), we only support two
                        // hv vectors: [0x11 0x11 0x11 0x11] and [0x22 0x11 0x11 0x22].
                        // Theoretically, 4-component JPEG images could mix and match hv values
                        // but in practice, those two combinations are the only ones in use,
                        // and it simplifies the applyBlack code below if we can assume that:
                        // - for CMYK, the C and K channels have full samples, and if the M
                        // and Y channels subsample, they subsample both horizontally and
                        // vertically.
                        // - for YCbCrK, the Y and K channels have full samples.
                        switch (i)
                        {
                            case 0:
                                if (hv != 0x11 && hv != 0x22) throw new ImageFormatException("unsupported subsampling ratio");
                                break;
                            case 1:
                            case 2:
                                if (hv != 0x11) throw new ImageFormatException("unsupported subsampling ratio");
                                break;
                            case 3:
                                if (this.comp[0].HorizontalFactor != h || this.comp[0].VerticalFactor != v) throw new ImageFormatException("unsupported subsampling ratio");
                                break;
                        }

                        break;
                }

                this.comp[i].HorizontalFactor = h;
                this.comp[i].VerticalFactor = v;
            }
        }

        // Specified in section B.2.4.1.
        private void processDQT(int n)
        {
            while (n > 0)
            {
                bool done = false;

                n--;
                byte x = this.ReadByte();
                byte tq = (byte)(x & 0x0f);
                if (tq > maxTq) throw new ImageFormatException("bad Tq value");

                switch (x >> 4)
                {
                    case 0:
                        if (n < Block.BlockSize)
                        {
                            done = true;
                            break;
                        }

                        n -= Block.BlockSize;
                        this.ReadFull(this.tmp, 0, Block.BlockSize);

                        for (int i = 0; i < Block.BlockSize; i++) this.quant[tq][i] = this.tmp[i];
                        break;
                    case 1:
                        if (n < 2 * Block.BlockSize)
                        {
                            done = true;
                            break;
                        }

                        n -= 2 * Block.BlockSize;
                        this.ReadFull(this.tmp, 0, 2 * Block.BlockSize);

                        for (int i = 0; i < Block.BlockSize; i++) this.quant[tq][i] = ((int)this.tmp[2 * i] << 8) | (int)this.tmp[2 * i + 1];
                        break;
                    default:
                        throw new ImageFormatException("bad Pq value");
                }

                if (done) break;
            }

            if (n != 0) throw new ImageFormatException("DQT has wrong length");
        }

        // Specified in section B.2.4.4.
        private void processDRI(int n)
        {
            if (n != 2) throw new ImageFormatException("DRI has wrong length");

            this.ReadFull(this.tmp, 0, 2);
            this.restartInterval = ((int)this.tmp[0] << 8) + (int)this.tmp[1];
        }

        private void ProcessApp0Marker(int n)
        {
            if (n < 5)
            {
                this.ignore(n);
                return;
            }

            this.ReadFull(this.tmp, 0, 13);
            n -= 13;

            // TODO: We should be using constants for this.
            this.isJfif = this.tmp[0] == 'J'
                     && this.tmp[1] == 'F'
                     && this.tmp[2] == 'I'
                     && this.tmp[3] == 'F'
                     && this.tmp[4] == '\x00';

            if (this.isJfif)
            {
                this.horizontalResolution = (short)(this.tmp[9] + (this.tmp[10] << 8));
                this.verticalResolution = (short)(this.tmp[11] + (this.tmp[12] << 8));
            }

            if (n > 0)
            {
                this.ignore(n);
            }
        }

        private void ProcessApp1Marker<TColor, TPacked>(int n, Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            if (n < 6)
            {
                this.ignore(n);
                return;
            }

            byte[] profile = new byte[n];
            this.ReadFull(profile, 0, n);

            if (profile[0] == 'E' &&
                profile[1] == 'x' &&
                profile[2] == 'i' &&
                profile[3] == 'f' &&
                profile[4] == '\0' &&
                profile[5] == '\0')
            {
                image.ExifProfile = new ExifProfile(profile);
            }
        }

        private void ProcessApp14Marker(int n)
        {
            if (n < 12)
            {
                this.ignore(n);
                return;
            }

            this.ReadFull(this.tmp, 0, 12);
            n -= 12;

            if (this.tmp[0] == 'A' && this.tmp[1] == 'd' && this.tmp[2] == 'o' && this.tmp[3] == 'b'
                && this.tmp[4] == 'e')
            {
                this.adobeTransformValid = true;
                this.adobeTransform = this.tmp[11];
            }

            if (n > 0)
            {
                this.ignore(n);
            }
        }

        /// <summary>
        /// Decodes the image from the specified this._stream and sets
        /// the data to image.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="stream">The stream, where the image should be
        /// decoded from. Cannot be null (Nothing in Visual Basic).</param>
        /// <param name="image">The image, where the data should be set to.
        /// Cannot be null (Nothing in Visual Basic).</param>
        /// <param name="configOnly">Whether to decode metadata only.</param>
        public void Decode<TColor, TPacked>(Image<TColor, TPacked> image, Stream stream, bool configOnly)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            this.inputStream = stream;

            // Check for the Start Of Image marker.
            this.ReadFull(this.tmp, 0, 2);
            if (this.tmp[0] != JpegConstants.Markers.XFF || this.tmp[1] != JpegConstants.Markers.SOI)
            {
                throw new ImageFormatException("Missing SOI marker.");
            }

            // Process the remaining segments until the End Of Image marker.
            while (true)
            {
                this.ReadFull(this.tmp, 0, 2);
                while (this.tmp[0] != 0xff)
                {
                    // Strictly speaking, this is a format error. However, libjpeg is
                    // liberal in what it accepts. As of version 9, next_marker in
                    // jdmarker.c treats this as a warning (JWRN_EXTRANEOUS_DATA) and
                    // continues to decode the stream. Even before next_marker sees
                    // extraneous data, jpeg_fill_bit_buffer in jdhuff.c reads as many
                    // bytes as it can, possibly past the end of a scan's data. It
                    // effectively puts back any markers that it overscanned (e.g. an
                    // "\xff\xd9" EOI marker), but it does not put back non-marker data,
                    // and thus it can silently ignore a small number of extraneous
                    // non-marker bytes before next_marker has a chance to see them (and
                    // print a warning).
                    // We are therefore also liberal in what we accept. Extraneous data
                    // is silently ignore
                    // This is similar to, but not exactly the same as, the restart
                    // mechanism within a scan (the RST[0-7] markers).
                    // Note that extraneous 0xff bytes in e.g. SOS data are escaped as
                    // "\xff\x00", and so are detected a little further down below.
                    this.tmp[0] = this.tmp[1];
                    this.tmp[1] = this.ReadByte();
                }

                byte marker = this.tmp[1];
                if (marker == 0)
                {
                    // Treat "\xff\x00" as extraneous data.
                    continue;
                }

                while (marker == 0xff)
                {
                    // Section B.1.1.2 says, "Any marker may optionally be preceded by any
                    // number of fill bytes, which are bytes assigned code X'FF'".
                    marker = this.ReadByte();
                }

                // End Of Image.
                if (marker == JpegConstants.Markers.EOI)
                {
                    break;
                }

                if (JpegConstants.Markers.RST0 <= marker && marker <= JpegConstants.Markers.RST7)
                {
                    // Figures B.2 and B.16 of the specification suggest that restart markers should
                    // only occur between Entropy Coded Segments and not after the final ECS.
                    // However, some encoders may generate incorrect JPEGs with a final restart
                    // marker. That restart marker will be seen here instead of inside the ProcessSOS
                    // method, and is ignored as a harmless error. Restart markers have no extra data,
                    // so we check for this before we read the 16-bit length of the segment.
                    continue;
                }

                // Read the 16-bit length of the segment. The value includes the 2 bytes for the
                // length itself, so we subtract 2 to get the number of remaining bytes.
                this.ReadFull(this.tmp, 0, 2);
                int n = ((int)this.tmp[0] << 8) + (int)this.tmp[1] - 2;
                if (n < 0)
                {
                    throw new ImageFormatException("Short segment length.");
                }

                switch (marker)
                {
                    case JpegConstants.Markers.SOF0:
                    case JpegConstants.Markers.SOF1:
                    case JpegConstants.Markers.SOF2:
                        this.isProgressive = marker == JpegConstants.Markers.SOF2;
                        this.ProcessSOF(n);
                        if (configOnly && this.isJfif)
                        {
                            return;
                        }

                        break;
                    case JpegConstants.Markers.DHT:
                        if (configOnly)
                        {
                            this.ignore(n);
                        }
                        else
                        {
                            this.ProcessDHT(n);
                        }

                        break;
                    case JpegConstants.Markers.DQT:
                        if (configOnly)
                        {
                            this.ignore(n);
                        }
                        else this.processDQT(n);
                        break;
                    case JpegConstants.Markers.SOS:
                        if (configOnly)
                        {
                            return;
                        }

                        this.ProcessStartOfScan(n);
                        break;
                    case JpegConstants.Markers.DRI:
                        if (configOnly)
                        {
                            this.ignore(n);
                        }
                        else
                        {
                            this.processDRI(n);
                        }

                        break;
                    case JpegConstants.Markers.APP0:
                        this.ProcessApp0Marker(n);
                        break;
                    case JpegConstants.Markers.APP1:
                        this.ProcessApp1Marker(n, image);
                        break;
                    case JpegConstants.Markers.APP14:
                        this.ProcessApp14Marker(n);
                        break;
                    default:
                        if (JpegConstants.Markers.APP0 <= marker && marker <= JpegConstants.Markers.APP15 || marker == JpegConstants.Markers.COM)
                        {
                            this.ignore(n);
                        }
                        else if (marker < JpegConstants.Markers.SOF0)
                        {
                            // See Table B.1 "Marker code assignments".
                            throw new ImageFormatException("Unknown marker");
                        }
                        else
                        {
                            throw new ImageFormatException("Unknown marker");
                        }

                        break;
                }
            }

            if (this.grayImage != null)
            {
                this.ConvertFromGrayScale(this.widthG, this.heightG, image);
            }
            else if (this.ycbcrImage != null)
            {
                if (this.componentCount == 4)
                {
                    this.ConvertFromCmyk(this.widthG, this.heightG, image);

                    // We have 3 components now.
                    this.componentCount = 3;
                    return;
                }

                if (this.componentCount == 3)
                {
                    if (this.IsRGB())
                    {
                        this.ConvertFromRGB(this.widthG, this.heightG, image);
                        return;
                    }

                    this.ConvertFromYCbCr(this.widthG, this.heightG, image);
                    return;
                }

                throw new ImageFormatException("JpegDecoder only supports RGB, CMYK and Grayscale color spaces.");
            }
            else
            {
                throw new ImageFormatException("Missing SOS marker.");
            }
        }

        /// <summary>
        /// Converts the image from the original Cmyk image pixels.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <param name="image">The image.</param>
        private void ConvertFromCmyk<TColor, TPacked>(int width, int height, Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            if (!adobeTransformValid)
            {
                throw new ImageFormatException("Unknown color model: 4-component JPEG doesn't have Adobe APP14 metadata");
            }

            if (adobeTransform != adobeTransformUnknown)
            {
                int scale = comp[0].HorizontalFactor / comp[1].HorizontalFactor;

                TColor[] pixels = new TColor[width * height];

                Parallel.For(
                    0,
                    height,
                    y =>
                    {
                        int yo = ycbcrImage.get_row_y_offset(y);
                        int co = ycbcrImage.get_row_c_offset(y);

                        for (int x = 0; x < width; x++)
                        {
                            byte yy = ycbcrImage.pix_y[yo + x];
                            byte cb = ycbcrImage.pix_cb[co + (x / scale)];
                            byte cr = ycbcrImage.pix_cr[co + (x / scale)];

                            int index = (y * width) + x;

                            // Implicit casting FTW
                            Color color = new YCbCr(yy, cb, cr);
                            int keyline = 255 - blackPix[y * blackStride + x];
                            Color final = new Cmyk(color.R / 255F, color.G / 255F, color.B / 255F, keyline / 255F);

                            TColor packed = default(TColor);
                            packed.PackFromVector4(final.ToVector4());
                            pixels[index] = packed;
                        }
                    });

                image.SetPixels(width, height, pixels);
            }
        }

        /// <summary>
        /// Converts the image from the original grayscale image pixels.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>long, float.</example></typeparam>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <param name="image">The image.</param>
        private void ConvertFromGrayScale<TColor, TPacked>(int width, int height, Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            TColor[] pixels = new TColor[width * height];

            Parallel.For(
                0,
                height,
                Bootstrapper.Instance.ParallelOptions,
                y =>
                {
                    int yoff = this.grayImage.GetRowOffset(y);
                    for (int x = 0; x < width; x++)
                    {
                        int offset = (y * width) + x;
                        byte rgb = this.grayImage.Pixels[yoff + x];

                        TColor packed = default(TColor);
                        packed.PackFromVector4(new Color(rgb, rgb, rgb).ToVector4());
                        pixels[offset] = packed;
                    }
                });

            image.SetPixels(width, height, pixels);
            this.AssignResolution(image);
        }

        /// <summary>
        /// Converts the image from the original YCbCr image pixels.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <param name="image">The image.</param>
        private void ConvertFromYCbCr<TColor, TPacked>(int width, int height, Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            int scale = this.comp[0].HorizontalFactor / this.comp[1].HorizontalFactor;

            TColor[] pixels = new TColor[width * height];

            Parallel.For(
                0,
                height,
                Bootstrapper.Instance.ParallelOptions,
                y =>
                    {
                        int yo = this.ycbcrImage.get_row_y_offset(y);
                        int co = this.ycbcrImage.get_row_c_offset(y);

                        for (int x = 0; x < width; x++)
                        {
                            byte yy = this.ycbcrImage.pix_y[yo + x];
                            byte cb = this.ycbcrImage.pix_cb[co + (x / scale)];
                            byte cr = this.ycbcrImage.pix_cr[co + (x / scale)];

                            int index = (y * width) + x;

                            // Implicit casting FTW
                            Color color = new YCbCr(yy, cb, cr);
                            TColor packed = default(TColor);
                            packed.PackFromVector4(color.ToVector4());
                            pixels[index] = packed;
                        }
                    });

            image.SetPixels(width, height, pixels);
            this.AssignResolution(image);
        }

        /// <summary>
        /// Converts the image from the original RBG image pixels.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="width">The image width.</param>
        /// <param name="height">The height.</param>
        /// <param name="image">The image.</param>
        private void ConvertFromRGB<TColor, TPacked>(int width, int height, Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            int scale = this.comp[0].HorizontalFactor / this.comp[1].HorizontalFactor;
            TColor[] pixels = new TColor[width * height];

            Parallel.For(
                0,
                height,
                Bootstrapper.Instance.ParallelOptions,
                y =>
                    {
                        int yo = this.ycbcrImage.get_row_y_offset(y);
                        int co = this.ycbcrImage.get_row_c_offset(y);

                        for (int x = 0; x < width; x++)
                        {
                            byte red = this.ycbcrImage.pix_y[yo + x];
                            byte green = this.ycbcrImage.pix_cb[co + (x / scale)];
                            byte blue = this.ycbcrImage.pix_cr[co + (x / scale)];

                            int index = (y * width) + x;
                            TColor packed = default(TColor);
                            packed.PackFromVector4(new Color(red, green, blue).ToVector4());

                            pixels[index] = packed;
                        }
                    });

            image.SetPixels(width, height, pixels);
            this.AssignResolution(image);
        }

        /// <summary>
        /// Assigns the horizontal and vertical resolution to the image if it has a JFIF header.
        /// </summary>
        /// <typeparam name="TColor">The pixel format.</typeparam>
        /// <typeparam name="TPacked">The packed format. <example>uint, long, float.</example></typeparam>
        /// <param name="image">The image to assign the resolution to.</param>
        private void AssignResolution<TColor, TPacked>(Image<TColor, TPacked> image)
            where TColor : IPackedVector<TPacked>
            where TPacked : struct
        {
            if (this.isJfif && this.horizontalResolution > 0 && this.verticalResolution > 0)
            {
                image.HorizontalResolution = this.horizontalResolution;
                image.VerticalResolution = this.verticalResolution;
            }
        }

        /// <summary>
        /// Processes the SOS (Start of scan marker).
        /// </summary>
        /// <remarks>
        /// TODO: This also needs some significant refactoring to follow a more OO format.
        /// </remarks>
        /// <param name="n">
        /// The first byte of the current image marker.
        /// </param>
        /// <exception cref="ImageFormatException">
        /// Missing SOF Marker
        /// SOS has wrong length
        /// </exception>
        private void ProcessStartOfScan(int n)
        {
            if (this.componentCount == 0)
            {
                throw new ImageFormatException("missing SOF marker");
            }

            if (n < 6 || 4 + (2 * this.componentCount) < n || n % 2 != 0)
            {
                throw new ImageFormatException("SOS has wrong length");
            }

            this.ReadFull(this.tmp, 0, n);
            byte lnComp = this.tmp[0];

            if (n != 4 + (2 * lnComp))
            {
                throw new ImageFormatException("SOS length inconsistent with number of components");
            }

            Scan[] scan = new Scan[maxComponents];
            int totalHV = 0;

            for (int i = 0; i < lnComp; i++)
            {
                // Component selector.
                int cs = this.tmp[1 + (2 * i)];
                int compIndex = -1;
                for (int j = 0; j < this.componentCount; j++)
                {
                    Component compv = this.comp[j];
                    if (cs == compv.Identifier)
                    {
                        compIndex = j;
                    }
                }

                if (compIndex < 0)
                {
                    throw new ImageFormatException("Unknown component selector");
                }

                scan[i].compIndex = (byte)compIndex;

                // Section B.2.3 states that "the value of Cs_j shall be different from
                // the values of Cs_1 through Cs_(j-1)". Since we have previously
                // verified that a frame's component identifiers (C_i values in section
                // B.2.2) are unique, it suffices to check that the implicit indexes
                // into comp are unique.
                for (int j = 0; j < i; j++)
                {
                    if (scan[i].compIndex == scan[j].compIndex)
                    {
                        throw new ImageFormatException("Repeated component selector");
                    }
                }

                totalHV += this.comp[compIndex].HorizontalFactor * this.comp[compIndex].VerticalFactor;

                scan[i].td = (byte)(this.tmp[2 + (2 * i)] >> 4);
                if (scan[i].td > maxTh)
                {
                    throw new ImageFormatException("bad Td value");
                }

                scan[i].ta = (byte)(this.tmp[2 + (2 * i)] & 0x0f);
                if (scan[i].ta > maxTh)
                {
                    throw new ImageFormatException("bad Ta value");
                }
            }

            // Section B.2.3 states that if there is more than one component then the
            // total H*V values in a scan must be <= 10.
            if (this.componentCount > 1 && totalHV > 10)
            {
                throw new ImageFormatException("Total sampling factors too large.");
            }

            // zigStart and zigEnd are the spectral selection bounds.
            // ah and al are the successive approximation high and low values.
            // The spec calls these values Ss, Se, Ah and Al.
            // For progressive JPEGs, these are the two more-or-less independent
            // aspects of progression. Spectral selection progression is when not
            // all of a block's 64 DCT coefficients are transmitted in one pass.
            // For example, three passes could transmit coefficient 0 (the DC
            // component), coefficients 1-5, and coefficients 6-63, in zig-zag
            // order. Successive approximation is when not all of the bits of a
            // band of coefficients are transmitted in one pass. For example,
            // three passes could transmit the 6 most significant bits, followed
            // by the second-least significant bit, followed by the least
            // significant bit.
            // For baseline JPEGs, these parameters are hard-coded to 0/63/0/0.
            int zigStart = 0;
            int zigEnd = Block.BlockSize - 1;
            int ah = 0;
            int al = 0;

            if (this.isProgressive)
            {
                zigStart = (int)this.tmp[1 + (2 * lnComp)];
                zigEnd = (int)this.tmp[2 + (2 * lnComp)];
                ah = (int)(this.tmp[3 + (2 * lnComp)] >> 4);
                al = (int)(this.tmp[3 + (2 * lnComp)] & 0x0f);

                if ((zigStart == 0 && zigEnd != 0) || zigStart > zigEnd || Block.BlockSize <= zigEnd)
                {
                    throw new ImageFormatException("Bad spectral selection bounds");
                }

                if (zigStart != 0 && lnComp != 1)
                {
                    throw new ImageFormatException("Progressive AC coefficients for more than one component");
                }

                if (ah != 0 && ah != al + 1)
                {
                    throw new ImageFormatException("Bad successive approximation values");
                }
            }

            // mxx and myy are the number of MCUs (Minimum Coded Units) in the image.
            int h0 = this.comp[0].HorizontalFactor;
            int v0 = this.comp[0].VerticalFactor;
            int mxx = (this.widthG + (8 * h0) - 1) / (8 * h0);
            int myy = (this.heightG + (8 * v0) - 1) / (8 * v0);

            if (this.grayImage == null && this.ycbcrImage == null)
            {
                this.MakeImage(mxx, myy);
            }

            if (this.isProgressive)
            {
                for (int i = 0; i < lnComp; i++)
                {
                    int compIndex = scan[i].compIndex;
                    if (this.progCoeffs[compIndex] == null)
                    {
                        this.progCoeffs[compIndex] =
                            new Block[mxx * myy * this.comp[compIndex].HorizontalFactor * this.comp[compIndex].VerticalFactor];

                        for (int j = 0; j < this.progCoeffs[compIndex].Length; j++)
                        {
                            this.progCoeffs[compIndex][j] = new Block();
                        }
                    }
                }
            }

            this.bits = new bits_class();

            int mcu = 0;
            byte expectedRst = JpegConstants.Markers.RST0;

            // b is the decoded coefficients, in natural (not zig-zag) order.
            Block b = new Block();
            int[] dc = new int[maxComponents];

            // bx and by are the location of the current block, in units of 8x8
            // blocks: the third block in the first row has (bx, by) = (2, 0).
            int bx, by, blockCount = 0;

            for (int my = 0; my < myy; my++)
            {
                for (int mx = 0; mx < mxx; mx++)
                {
                    for (int i = 0; i < lnComp; i++)
                    {
                        int compIndex = scan[i].compIndex;
                        int hi = this.comp[compIndex].HorizontalFactor;
                        int vi = this.comp[compIndex].VerticalFactor;
                        Block qt = this.quant[this.comp[compIndex].Selector];

                        for (int j = 0; j < hi * vi; j++)
                        {
                            // The blocks are traversed one MCU at a time. For 4:2:0 chroma
                            // subsampling, there are four Y 8x8 blocks in every 16x16 MCU.
                            // For a baseline 32x16 pixel image, the Y blocks visiting order is:
                            // 0 1 4 5
                            // 2 3 6 7
                            // For progressive images, the interleaved scans (those with nComp > 1)
                            // are traversed as above, but non-interleaved scans are traversed left
                            // to right, top to bottom:
                            // 0 1 2 3
                            // 4 5 6 7
                            // Only DC scans (zigStart == 0) can be interleave AC scans must have
                            // only one component.
                            // To further complicate matters, for non-interleaved scans, there is no
                            // data for any blocks that are inside the image at the MCU level but
                            // outside the image at the pixel level. For example, a 24x16 pixel 4:2:0
                            // progressive image consists of two 16x16 MCUs. The interleaved scans
                            // will process 8 Y blocks:
                            // 0 1 4 5
                            // 2 3 6 7
                            // The non-interleaved scans will process only 6 Y blocks:
                            // 0 1 2
                            // 3 4 5
                            if (lnComp != 1)
                            {
                                bx = hi * mx + j % hi;
                                by = vi * my + j / hi;
                            }
                            else
                            {
                                int q = mxx * hi;
                                bx = blockCount % q;
                                by = blockCount / q;
                                blockCount++;
                                if (bx * 8 >= this.widthG || by * 8 >= this.heightG)
                                {
                                    continue;
                                }
                            }

                            // Load the previous partially decoded coefficients, if applicable.
                            if (this.isProgressive)
                            {
                                b = this.progCoeffs[compIndex][by * mxx * hi + bx];
                            }
                            else
                            {
                                b = new Block();
                            }

                            if (ah != 0)
                            {
                                this.Refine(b, this.huff[acTable, scan[i].ta], zigStart, zigEnd, 1 << al);
                            }
                            else
                            {
                                int zig = zigStart;
                                if (zig == 0)
                                {
                                    zig++;

                                    // Decode the DC coefficient, as specified in section F.2.2.1.
                                    byte value = this.decodeHuffman(this.huff[dcTable, scan[i].td]);
                                    if (value > 16)
                                    {
                                        throw new ImageFormatException("Excessive DC component");
                                    }

                                    int dcDelta = this.ReceiveExtend(value);
                                    dc[compIndex] += dcDelta;
                                    b[0] = dc[compIndex] << al;
                                }

                                if (zig <= zigEnd && this.eobRun > 0)
                                {
                                    this.eobRun--;
                                }
                                else
                                {
                                    // Decode the AC coefficients, as specified in section F.2.2.2.
                                    Huffman huffv = this.huff[acTable, scan[i].ta];
                                    for (; zig <= zigEnd; zig++)
                                    {
                                        byte value = this.decodeHuffman(huffv);
                                        byte val0 = (byte)(value >> 4);
                                        byte val1 = (byte)(value & 0x0f);
                                        if (val1 != 0)
                                        {
                                            zig += val0;
                                            if (zig > zigEnd)
                                            {
                                                break;
                                            }

                                            int ac = this.ReceiveExtend(val1);
                                            b[Unzig[zig]] = ac << al;
                                        }
                                        else
                                        {
                                            if (val0 != 0x0f)
                                            {
                                                this.eobRun = (ushort)(1 << val0);
                                                if (val0 != 0)
                                                {
                                                    this.eobRun |= (ushort)this.decodeBits(val0);
                                                }

                                                this.eobRun--;
                                                break;
                                            }

                                            zig += 0x0f;
                                        }
                                    }
                                }
                            }

                            if (this.isProgressive)
                            {
                                if (zigEnd != Block.BlockSize - 1 || al != 0)
                                {
                                    // We haven't completely decoded this 8x8 block. Save the coefficients.
                                    this.progCoeffs[compIndex][by * mxx * hi + bx] = b;

                                    // At this point, we could execute the rest of the loop body to dequantize and
                                    // perform the inverse DCT, to save early stages of a progressive image to the
                                    // *image.YCbCr buffers (the whole point of progressive encoding), but in Go,
                                    // the jpeg.Decode function does not return until the entire image is decoded,
                                    // so we "continue" here to avoid wasted computation.
                                    continue;
                                }
                            }

                            // Dequantize, perform the inverse DCT and store the block to the image.
                            for (int zig = 0; zig < Block.BlockSize; zig++)
                            {
                                b[Unzig[zig]] *= qt[zig];
                            }

                            IDCT.Transform(b);

                            byte[] dst;
                            int offset;
                            int stride;

                            if (this.componentCount == 1)
                            {
                                dst = this.grayImage.Pixels;
                                stride = this.grayImage.Stride;
                                offset = this.grayImage.Offset + 8 * (by * this.grayImage.Stride + bx);
                            }
                            else
                            {
                                switch (compIndex)
                                {
                                    case 0:
                                        dst = this.ycbcrImage.pix_y;
                                        stride = this.ycbcrImage.y_stride;
                                        offset = this.ycbcrImage.y_offset + 8 * (by * this.ycbcrImage.y_stride + bx);
                                        break;

                                    case 1:
                                        dst = this.ycbcrImage.pix_cb;
                                        stride = this.ycbcrImage.c_stride;
                                        offset = this.ycbcrImage.c_offset + 8 * (by * this.ycbcrImage.c_stride + bx);
                                        break;

                                    case 2:
                                        dst = this.ycbcrImage.pix_cr;
                                        stride = this.ycbcrImage.c_stride;
                                        offset = this.ycbcrImage.c_offset + 8 * (by * this.ycbcrImage.c_stride + bx);
                                        break;

                                    case 3:

                                        dst = this.blackPix;
                                        stride = this.blackStride;
                                        offset = 8 * (by * blackStride + bx);
                                        break;

                                    default:
                                        throw new ImageFormatException("Too many components");
                                }
                            }

                            // Level shift by +128, clip to [0, 255], and write to dst.
                            for (int y = 0; y < 8; y++)
                            {
                                int y8 = y * 8;
                                int yStride = y * stride;

                                for (int x = 0; x < 8; x++)
                                {
                                    int c = b[y8 + x];
                                    if (c < -128)
                                    {
                                        c = 0;
                                    }
                                    else if (c > 127)
                                    {
                                        c = 255;
                                    }
                                    else
                                    {
                                        c += 128;
                                    }

                                    dst[yStride + x + offset] = (byte)c;
                                }
                            }
                        }
                        // for j
                    }
                    // for i

                    mcu++;

                    if (this.restartInterval > 0 && mcu % this.restartInterval == 0 && mcu < mxx * myy)
                    {
                        // A more sophisticated decoder could use RST[0-7] markers to resynchronize from corrupt input,
                        // but this one assumes well-formed input, and hence the restart marker follows immediately.
                        this.ReadFull(this.tmp, 0, 2);
                        if (this.tmp[0] != 0xff || this.tmp[1] != expectedRst)
                        {
                            throw new ImageFormatException("Bad RST marker");
                        }

                        expectedRst++;
                        if (expectedRst == JpegConstants.Markers.RST7 + 1)
                        {
                            expectedRst = JpegConstants.Markers.RST0;
                        }

                        // Reset the Huffman decoder.
                        this.bits = new bits_class();

                        // Reset the DC components, as per section F.2.1.3.1.
                        dc = new int[maxComponents];

                        // Reset the progressive decoder state, as per section G.1.2.2.
                        this.eobRun = 0;
                    }
                }
                // for mx
            }
            // for my
        }

        /// <summary>
        /// Decodes a successive approximation refinement block, as specified in section G.1.2.
        /// </summary>
        /// <param name="b"></param>
        /// <param name="h"></param>
        /// <param name="zigStart"></param>
        /// <param name="zigEnd"></param>
        /// <param name="delta"></param>
        private void Refine(Block b, Huffman h, int zigStart, int zigEnd, int delta)
        {
            // Refining a DC component is trivial.
            if (zigStart == 0)
            {
                if (zigEnd != 0)
                {
                    throw new ImageFormatException("Invalid state for zig DC component");
                }

                bool bit = this.decodeBit();
                if (bit)
                {
                    b[0] |= delta;
                }

                return;
            }

            // Refining AC components is more complicated; see sections G.1.2.2 and G.1.2.3.
            int zig = zigStart;
            if (this.eobRun == 0)
            {
                for (; zig <= zigEnd; zig++)
                {
                    bool done = false;
                    int z = 0;
                    byte val = this.decodeHuffman(h);
                    int val0 = val >> 4;
                    int val1 = val & 0x0f;

                    switch (val1)
                    {
                        case 0:
                            if (val0 != 0x0f)
                            {
                                this.eobRun = (ushort)(1 << val0);
                                if (val0 != 0)
                                {
                                    uint bits = this.decodeBits(val0);
                                    this.eobRun |= (ushort)bits;
                                }

                                done = true;
                            }

                            break;
                        case 1:
                            z = delta;
                            bool bit = this.decodeBit();
                            if (!bit)
                            {
                                z = -z;
                            }

                            break;
                        default:
                            throw new ImageFormatException("Unexpected Huffman code");
                    }

                    if (done) break;

                    zig = this.RefineNonZeroes(b, zig, zigEnd, val0, delta);
                    if (zig > zigEnd)
                    {
                        throw new ImageFormatException($"Too many coefficients {zig} > {zigEnd}");
                    }

                    if (z != 0)
                    {
                        b[Unzig[zig]] = z;
                    }
                }
            }

            if (this.eobRun > 0)
            {
                this.eobRun--;
                this.RefineNonZeroes(b, zig, zigEnd, -1, delta);
            }
        }

        // refineNonZeroes refines non-zero entries of b in zig-zag order. If nz >= 0,
        // the first nz zero entries are skipped over.
        private int RefineNonZeroes(Block b, int zig, int zigEnd, int nz, int delta)
        {
            for (; zig <= zigEnd; zig++)
            {
                int u = Unzig[zig];
                if (b[u] == 0)
                {
                    if (nz == 0) break;
                    nz--;
                    continue;
                }

                bool bit = this.decodeBit();
                if (!bit)
                {
                    continue;
                }

                if (b[u] >= 0)
                {
                    b[u] += delta;
                }
                else
                {
                    b[u] -= delta;
                }
            }

            return zig;
        }

        private void MakeImage(int mxx, int myy)
        {
            if (this.componentCount == 1)
            {
                GrayImage m = new GrayImage(8 * mxx, 8 * myy);
                this.grayImage = m.Subimage(0, 0, this.widthG, this.heightG);
            }
            else
            {
                int h0 = this.comp[0].HorizontalFactor;
                int v0 = this.comp[0].VerticalFactor;
                int horizontalRatio = h0 / this.comp[1].HorizontalFactor;
                int verticalRatio = v0 / this.comp[1].VerticalFactor;

                YCbCrImage.YCbCrSubsampleRatio ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio444;
                switch ((horizontalRatio << 4) | verticalRatio)
                {
                    case 0x11:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio444;
                        break;
                    case 0x12:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio440;
                        break;
                    case 0x21:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio422;
                        break;
                    case 0x22:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio420;
                        break;
                    case 0x41:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio411;
                        break;
                    case 0x42:
                        ratio = YCbCrImage.YCbCrSubsampleRatio.YCbCrSubsampleRatio410;
                        break;
                }

                YCbCrImage m = new YCbCrImage(8 * h0 * mxx, 8 * v0 * myy, ratio);
                this.ycbcrImage = m.subimage(0, 0, this.widthG, this.heightG);

                if (this.componentCount == 4)
                {
                    int h3 = this.comp[3].HorizontalFactor;
                    int v3 = this.comp[3].VerticalFactor;
                    this.blackPix = new byte[8 * h3 * mxx * 8 * v3 * myy];
                    this.blackStride = 8 * h3 * mxx;
                }
            }
        }

        /// <summary>
        /// Returns a value indicating whether the image in an RGB image.
        /// </summary>
        /// <returns>
        /// The <see cref="bool"/>.
        /// </returns>
        private bool IsRGB()
        {
            if (this.isJfif)
            {
                return false;
            }

            if (this.adobeTransformValid && this.adobeTransform == adobeTransformUnknown)
            {
                // http://www.sno.phy.queensu.ca/~phil/exiftool/TagNames/JPEG.html#Adobe
                // says that 0 means Unknown (and in practice RGB) and 1 means YCbCr.
                return true;
            }

            return this.comp[0].Identifier == 'R' && this.comp[1].Identifier == 'G' && this.comp[2].Identifier == 'B';
        }

        /// <summary>
        /// Represents a single color component
        /// </summary>
        private class Component
        {
            /// <summary>
            /// Gets or sets the horizontal sampling factor.
            /// </summary>
            public int HorizontalFactor { get; set; }

            /// <summary>
            /// Gets or sets the vertical sampling factor.
            /// </summary>
            public int VerticalFactor { get; set; }

            /// <summary>
            /// Gets or sets the identifier
            /// </summary>
            public byte Identifier { get; set; }

            /// <summary>
            /// Gets or sets the quantization table destination selector.
            /// </summary>
            public byte Selector { get; set; }
        }

        private class YCbCrImage
        {
            public enum YCbCrSubsampleRatio
            {
                YCbCrSubsampleRatio444,

                YCbCrSubsampleRatio422,

                YCbCrSubsampleRatio420,

                YCbCrSubsampleRatio440,

                YCbCrSubsampleRatio411,

                YCbCrSubsampleRatio410,
            }

            private static void YCbCrSize(int w, int h, YCbCrSubsampleRatio ratio, out int cw, out int ch)
            {
                switch (ratio)
                {
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio422:
                        cw = (w + 1) / 2;
                        ch = h;
                        break;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio420:
                        cw = (w + 1) / 2;
                        ch = (h + 1) / 2;
                        break;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio440:
                        cw = w;
                        ch = (h + 1) / 2;
                        break;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio411:
                        cw = (w + 3) / 4;
                        ch = h;
                        break;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio410:
                        cw = (w + 3) / 4;
                        ch = (h + 1) / 2;
                        break;
                    default:

                        // Default to 4:4:4 subsampling.
                        cw = w;
                        ch = h;
                        break;
                }
            }

            private YCbCrImage()
            {
            }

            public byte[] pix_y;

            public byte[] pix_cb;

            public byte[] pix_cr;

            public int y_stride;

            public int c_stride;

            public int y_offset;

            public int c_offset;

            public int x;

            public int y;

            public int w;

            public int h;

            public YCbCrSubsampleRatio ratio;

            public YCbCrImage(int w, int h, YCbCrSubsampleRatio ratio)
            {
                int cw, ch;
                YCbCrSize(w, h, ratio, out cw, out ch);
                this.pix_y = new byte[w * h];
                this.pix_cb = new byte[cw * ch];
                this.pix_cr = new byte[cw * ch];
                this.ratio = ratio;
                this.y_stride = w;
                this.c_stride = cw;
                this.x = 0;
                this.y = 0;
                this.w = w;
                this.h = h;
            }

            public YCbCrImage subimage(int x, int y, int w, int h)
            {
                YCbCrImage ret = new YCbCrImage
                {
                    w = w,
                    h = h,
                    pix_y = this.pix_y,
                    pix_cb = this.pix_cb,
                    pix_cr = this.pix_cr,
                    ratio = this.ratio,
                    y_stride = this.y_stride,
                    c_stride = this.c_stride,
                    y_offset = y * this.y_stride + x,
                    c_offset = y * this.c_stride + x
                };
                return ret;
            }

            public int get_row_y_offset(int y)
            {
                return y * this.y_stride;
            }

            public int get_row_c_offset(int y)
            {
                switch (this.ratio)
                {
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio422:
                        return y * this.c_stride;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio420:
                        return (y / 2) * this.c_stride;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio440:
                        return (y / 2) * this.c_stride;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio411:
                        return y * this.c_stride;
                    case YCbCrSubsampleRatio.YCbCrSubsampleRatio410:
                        return (y / 2) * this.c_stride;
                    default:
                        return y * this.c_stride;
                }
            }
        }

        /// <summary>
        /// Represents a grayscale image
        /// </summary>
        private class GrayImage
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="GrayImage"/> class.
            /// </summary>
            /// <param name="width">The width.</param>
            /// <param name="height">The height.</param>
            public GrayImage(int width, int height)
            {
                this.Width = width;
                this.Height = height;
                this.Pixels = new byte[width * height];
                this.Stride = width;
                this.Offset = 0;
            }

            /// <summary>
            /// Prevents a default instance of the <see cref="GrayImage"/> class from being created.
            /// </summary>
            private GrayImage()
            {
            }

            /// <summary>
            /// Gets or sets the pixels.
            /// </summary>
            public byte[] Pixels { get; set; }

            /// <summary>
            /// Gets or sets the stride.
            /// </summary>
            public int Stride { get; set; }

            /// <summary>
            /// Gets or sets the horizontal position.
            /// </summary>
            public int X { get; set; }

            /// <summary>
            /// Gets or sets the vertical position.
            /// </summary>
            public int Y { get; set; }

            /// <summary>
            /// Gets or sets the width.
            /// </summary>
            public int Width { get; set; }

            /// <summary>
            /// Gets or sets the height.
            /// </summary>
            public int Height { get; set; }

            /// <summary>
            /// Gets or sets the offset
            /// </summary>
            public int Offset { get; set; }

            /// <summary>
            /// Gets an image made up of a subset of the originals pixels.
            /// </summary>
            /// <param name="x">The x-coordinate of the image.</param>
            /// <param name="y">The y-coordinate of the image.</param>
            /// <param name="width">The width.</param>
            /// <param name="height">The height.</param>
            /// <returns>
            /// The <see cref="GrayImage"/>.
            /// </returns>
            public GrayImage Subimage(int x, int y, int width, int height)
            {
                return new GrayImage
                {
                    Width = width,
                    Height = height,
                    Pixels = this.Pixels,
                    Stride = this.Stride,
                    Offset = (y * this.Stride) + x
                };
            }

            /// <summary>
            /// Gets the row offset at the given position
            /// </summary>
            /// <param name="y">The y-coordinate of the image.</param>
            /// <returns>The <see cref="int"/></returns>
            public int GetRowOffset(int y)
            {
                return this.Offset + (y * this.Stride);
            }
        }

        private class Huffman
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="Huffman"/> class. 
            /// </summary>
            public Huffman()
            {
                this.lut = new ushort[1 << LutSize];
                this.vals = new byte[MaxNCodes];
                this.minCodes = new int[MaxCodeLength];
                this.maxCodes = new int[MaxCodeLength];
                this.valsIndices = new int[MaxCodeLength];
                this.nCodes = 0;
            }

            // length is the number of codes in the tree.
            public int nCodes;

            // lut is the look-up table for the next lutSize bits in the bit-stream.
            // The high 8 bits of the uint16 are the encoded value. The low 8 bits
            // are 1 plus the code length, or 0 if the value is too large to fit in
            // lutSize bits.
            public ushort[] lut;

            // vals are the decoded values, sorted by their encoding.
            public byte[] vals;

            // minCodes[i] is the minimum code of length i, or -1 if there are no
            // codes of that length.
            public int[] minCodes;

            // maxCodes[i] is the maximum code of length i, or -1 if there are no
            // codes of that length.
            public int[] maxCodes;

            // valsIndices[i] is the index into vals of minCodes[i].
            public int[] valsIndices;
        }

        // bytes is a byte buffer, similar to a bufio.Reader, except that it
        // has to be able to unread more than 1 byte, due to byte stuffing.
        // Byte stuffing is specified in section F.1.2.3.
        private class bytes_class
        {
            public bytes_class()
            {
                this.buf = new byte[4096];
                this.i = 0;
                this.j = 0;
                this.nUnreadable = 0;
            }

            // buf[i:j] are the buffered bytes read from the underlying
            // io.Reader that haven't yet been passed further on.
            public byte[] buf;

            public int i;

            public int j;

            // nUnreadable is the number of bytes to back up i after
            // overshooting. It can be 0, 1 or 2.
            public int nUnreadable;
        }

        // bits holds the unprocessed bits that have been taken from the byte-stream.
        // The n least significant bits of a form the unread bits, to be read in MSB to
        // LSB order.
        private class bits_class
        {
            public uint a; // accumulator.

            public uint m; // mask. m==1<<(n-1) when n>0, with m==0 when n==0.

            public int n; // the number of unread bits in a.
        }

        private class MissingFF00Exception : Exception
        {
        }

        private class ShortHuffmanDataException : Exception
        {
        }

        private class EOFException : Exception
        {
        }

        private struct Scan
        {
            public byte compIndex;

            public byte td;

            public byte ta;
        }
    }
}
