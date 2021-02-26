# AudioIO
C# source code for input/output audio in WinForms/WPF.
The library consists of 2 classes.

## AudioInput
This class provides audio sampling feature via mic, audio line-in, etc.

```C#
string[] devices = AudioInput.FindDevices();
if (devices.Length == 0) return; // no device.

// create AudioInput with default device.
var device = new AudioInput(44100, 16, 2);

// start sampling
device.Start(data =>
{
    // called when each buffer becomes full
    Console.WriteLine(data.Length);
});

// release
this.FormClosing += (s, ev) =>
{
    device.Stop();
    device.Close();
};
```

## AudioOutput
This class provides audio playback feature from wave data.

```C#
string[] devices = AudioOutput.FindDevices();
if (devices.Length == 0) return; // no device.

// create AudioOutput with default device.
var device = new AudioOutput(44100, 16, 1);

// start writing.
device.WriteStart(() =>
{
    // called when each buffer becomes empty and request more data.
    const double freq = 1000;
    var sign_wave = WaveEx.SineWave(freq, 44100, short.MaxValue, 0).Select(x => (short)x).Take(44100).ToArray().ToLittleEndian();
    return sign_wave;
});

// release
this.FormClosing += (s, ev) =>
{
    device.WriteStop();
    device.Close();
};

static class WaveEx
{
    public static IEnumerable<double> SineWave(double freq, int rate, double amplitude, int phase)
    {
        while (true)
        {
            yield return Math.Sin(2 * Math.PI * freq * phase++ / rate) * amplitude;
        }
    }

    public static byte[] ToLittleEndian(this short[] data)
    {
        var result = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            result[(i * 2) + 0] = (byte)(data[i] & 0xFF);
            result[(i * 2) + 1] = (byte)(data[i] >> 8);
        }
        return result;
    }
}
```

# How to use
Add AudioIO.cs to your project.
