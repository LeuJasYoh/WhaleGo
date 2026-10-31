using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

/// <summary>Image-pixel -> client-pixel -> screen-pixel mapping (docs/PROTOCOL.md section 6).</summary>
public static class CoordinateMapper
{
    public static bool TryMapImageToClient(FrameBinding frame, int imageX, int imageY, out int clientX, out int clientY)
    {
        clientX = -1;
        clientY = -1;
        if (!frame.Actionable ||
            frame.ImageWidth <= 0 || frame.ImageHeight <= 0 ||
            frame.ClientWidth <= 0 || frame.ClientHeight <= 0)
        {
            return false;
        }

        if (imageX < 0 || imageX >= frame.ImageWidth || imageY < 0 || imageY >= frame.ImageHeight)
        {
            return false;
        }

        clientX = (int)Math.Round(imageX * (double)frame.ClientWidth / frame.ImageWidth, MidpointRounding.AwayFromZero);
        clientY = (int)Math.Round(imageY * (double)frame.ClientHeight / frame.ImageHeight, MidpointRounding.AwayFromZero);
        clientX = Math.Clamp(clientX, 0, frame.ClientWidth - 1);
        clientY = Math.Clamp(clientY, 0, frame.ClientHeight - 1);
        return true;
    }

    public static bool TryMapImageToScreen(FrameBinding frame, int imageX, int imageY, out int screenX, out int screenY)
    {
        screenX = -1;
        screenY = -1;
        if (!TryMapImageToClient(frame, imageX, imageY, out var clientX, out var clientY))
        {
            return false;
        }

        screenX = frame.ClientOrigin.Left + clientX;
        screenY = frame.ClientOrigin.Top + clientY;
        return true;
    }
}
