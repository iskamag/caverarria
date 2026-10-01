using System.Text.Json;

namespace Caverarria;

internal interface ICampaignEngine : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool HasPcmAudio { get; }
    int AudioSampleRate { get; }
    JsonElement Send(object request);
    JsonElement Resize(int width, int height);
    void CopyPixels(int layer, byte[] destination);
    void ReadAudio(byte[] destination);
}
