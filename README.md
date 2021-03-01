# AudioIO
C# source code for input/output audio wave data in WinForms/WPF.  
With only single CSharp source code. No external library required.

# How to use
Add AudioIO.cs to your project.  
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

const double freq = 1000;
var sign_wave = WaveEx.SineWave(freq, 44100, short.MaxValue, 0).Select(x => (short)x).Take(44100).ToArray().ToLittleEndian();
    
// create AudioOutput with default device.
var device = new AudioOutput(44100, 16, 1);

// start writing.
device.WriteStart(() =>
{
    // called when each buffer becomes empty and request more data.
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

# Example1
Record audio, and playback it.
```C#
var samplesPerSec = 44100; // try 'samplesPerSec = 8000', like a telephone!
var output = new GitHub.secile.Audio.AudioOutput(samplesPerSec, 16, 2);
var input = new GitHub.secile.Audio.AudioInput(samplesPerSec, 16, 2);

var bufferSize = input.BytesPerSec / 2; // You can hear your voice late. To reduce the delay, reduce bufferSize value. (ex: BytesPerSec / 10)
input.Start(data =>
{
    output.Write(data);
}, bufferSize);
```

# Example2
You can make MotionJPEG video recorder by using [MotionJPEGWriter](https://github.com/secile/MotionJPEGWriter/) and [UsbCamera](https://github.com/secile/UsbCamera/).

```C#
// create mjpeg writer.
var path = System.Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) + @"\test.avi";
var output = System.IO.File.OpenWrite(path);
var videoFormat = new GitHub.secile.Avi.AviWriter.VideoFormat() { Width = 640, Height = 480, FramesPerSec = 10 };
var audioFormat = new GitHub.secile.Avi.AviWriter.AudioFormat() { SamplesPerSec = 44100, BitsPerSample = 16, Channels = 2 };
var avi = new GitHub.secile.Avi.MjpegWriter(output, videoFormat, audioFormat);

// start video capture every 10 times per second.
var camera = new GitHub.secile.Video.UsbCamera(0, new Size(640, 480));
camera.Start();
var timer = new System.Timers.Timer(1000 / 10) { SynchronizingObject = this };
timer.Elapsed += (s, ev) => avi.AddImage(camera.GetBitmap());
timer.Start();

// start audio sampling every second.
var audioInput = new GitHub.secile.Audio.AudioInput(44100, 16, 2);
audioInput.Start(data => avi.AddAudio(data));

// release
this.FormClosing += (s, ev) =>
{
    timer.Stop();
    audioInput.Stop();
    audioInput.Close();
    camera.Stop();
    avi.Close();
};
```
