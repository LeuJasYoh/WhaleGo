namespace WhaleGo.Contracts.Model;

/// <summary>Integer rectangle in physical screen pixels (X right, Y down; corners are left/top inclusive, right/bottom exclusive).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static RectI FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"[{Left},{Top},{Right},{Bottom}]";
}
