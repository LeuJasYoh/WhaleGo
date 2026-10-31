namespace WhaleGo.Platform.Capture;

/// <summary>
/// PrintWindow result validation (D8/D20): sane dimensions + non-empty + not pure black + not a uniform (blank) surface.
/// Signature is the closed wire set nonblank_ok | blank | black | failed; only nonblank_ok may become frame.actionable=true.
/// </summary>
public static class CaptureValidator
{
    public const string SignatureNonblankOk = "nonblank_ok";
    public const string SignatureBlank = "blank";
    public const string SignatureBlack = "black";
    public const string SignatureFailed = "failed";

    public sealed record CaptureValidation(
        string Signature,
        bool SaneDimensions,
        bool NonEmpty,
        bool NotBlack,
        bool Uniform,
        bool WindowMinimized)
    {
        public bool Passed => string.Equals(Signature, SignatureNonblankOk, StringComparison.Ordinal);
    }

    public static CaptureValidation Validate(ReadOnlySpan<byte> bgra, int width, int height, int stride, bool windowMinimized = false)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * height)
        {
            return new CaptureValidation(SignatureFailed, false, false, false, false, windowMinimized);
        }

        if (width < 2 || height < 2)
        {
            return new CaptureValidation(SignatureFailed, false, false, false, false, windowMinimized);
        }

        if (windowMinimized)
        {
            return new CaptureValidation(SignatureFailed, true, true, true, false, true);
        }

        uint? first = null;
        var uniform = true;
        var anyNonBlack = false;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = row + (x * 4);
                var pixel = (uint)(bgra[index] | (bgra[index + 1] << 8) | (bgra[index + 2] << 16));
                if (pixel != 0)
                {
                    anyNonBlack = true;
                }

                if (first is null)
                {
                    first = pixel;
                }
                else if (uniform && pixel != first.Value)
                {
                    uniform = false;
                }
            }
        }

        if (!anyNonBlack)
        {
            return new CaptureValidation(SignatureBlack, true, true, false, false, false);
        }

        if (uniform)
        {
            return new CaptureValidation(SignatureBlank, true, true, true, true, false);
        }

        return new CaptureValidation(SignatureNonblankOk, true, true, true, false, false);
    }
}
