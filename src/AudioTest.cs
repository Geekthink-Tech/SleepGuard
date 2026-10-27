// audio-test: plays a system WAV in a loop for N seconds so that a real audio
// session exists on the default render endpoint (used to verify SleepGuard's
// "audio/video is playing -> postpone display-off" exception).
using System;
using System.IO;
using System.Media;
using System.Threading;

internal static class AudioTest
{
    private static int Main(string[] args)
    {
        int seconds = 45;
        if (args.Length > 0) int.TryParse(args[0], out seconds);

        string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media", "Alarm01.wav");
        if (!File.Exists(wav))
        {
            wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media", "Windows Notify System Generic.wav");
        }

        try
        {
            SoundPlayer player = new SoundPlayer(wav);
            player.PlayLooping();
            Thread.Sleep(seconds * 1000);
            player.Stop();
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "audio-test-err.txt"), e.ToString());
            return 1;
        }
    }
}
