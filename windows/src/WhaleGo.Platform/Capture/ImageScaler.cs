namespace WhaleGo.Platform.Capture;

/// <summary>Area-average downscaler for 32bpp BGRA buffers (only shrinks; screenshots never upscale).</summary>
internal static class ImageScaler
{
    public static byte[] Downscale(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Image dimensions must be positive.");
        }

        var sourceStride = sourceWidth * 4;
        if (source.Length < sourceStride * sourceHeight)
        {
            throw new ArgumentException("Source buffer is smaller than the declared dimensions.", nameof(source));
        }

        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
        {
            return (byte[])source.Clone();
        }

        var target = new byte[targetWidth * targetHeight * 4];
        for (var destinationY = 0; destinationY < targetHeight; destinationY++)
        {
            var sourceY0 = (int)((long)destinationY * sourceHeight / targetHeight);
            var sourceY1 = Math.Max(sourceY0 + 1, (int)((long)(destinationY + 1) * sourceHeight / targetHeight));
            if (sourceY1 > sourceHeight)
            {
                sourceY1 = sourceHeight;
            }

            for (var destinationX = 0; destinationX < targetWidth; destinationX++)
            {
                var sourceX0 = (int)((long)destinationX * sourceWidth / targetWidth);
                var sourceX1 = Math.Max(sourceX0 + 1, (int)((long)(destinationX + 1) * sourceWidth / targetWidth));
                if (sourceX1 > sourceWidth)
                {
                    sourceX1 = sourceWidth;
                }

                long blue = 0, green = 0, red = 0, alpha = 0;
                var count = 0;
                for (var y = sourceY0; y < sourceY1; y++)
                {
                    var rowOffset = y * sourceStride;
                    for (var x = sourceX0; x < sourceX1; x++)
                    {
                        var index = rowOffset + (x * 4);
                        blue += source[index];
                        green += source[index + 1];
                        red += source[index + 2];
                        alpha += source[index + 3];
                        count++;
                    }
                }

                var targetIndex = ((destinationY * targetWidth) + destinationX) * 4;
                target[targetIndex] = (byte)(blue / count);
                target[targetIndex + 1] = (byte)(green / count);
                target[targetIndex + 2] = (byte)(red / count);
                target[targetIndex + 3] = (byte)(alpha / count);
            }
        }

        return target;
    }
}
